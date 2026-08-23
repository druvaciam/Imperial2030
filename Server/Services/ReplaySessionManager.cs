using System.Collections.Concurrent;
using System.Text.Json;
using Imperial2030.Server.Controllers;
using Imperial2030.Server.Data;
using Imperial2030.Server.Helpers;
using Imperial2030.Server.Hubs;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Imperial2030.Server.Services;

public class ReplaySession
{
    public required Guid Id { get; init; }
    public required Guid SourceGameId { get; init; }
    public required Guid ReplayGameId { get; init; }
    public required ApplicationDbContext Context { get; set; }
    public required List<GameActionDto> Actions { get; init; }
    public int CurrentActionIndex { get; set; } = -1;

    /// <summary>
    /// Delay between visibly-applied actions, per session rather than per manager: each viewer sets
    /// their own playback speed and must not change anyone else's. Seeded from the manager's
    /// <see cref="ReplaySessionManager.PacingMs"/> when the session starts.
    /// </summary>
    public int PacingMs { get; set; } = Imperial2030.Shared.Constants.ReplaySpeed.DefaultPacingMs;

    public bool IsPaused { get; set; }
    public bool IsComplete { get; set; }
    public string? ErrorMessage { get; set; }
    public GameDetailDto? LatestSnapshot { get; set; }
    public CancellationTokenSource Cts { get; set; } = new();
    public Task? LoopTask { get; set; }
    public DateTime LastAccessedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Owns "Start Replay" playback sessions: purely in-memory, ephemeral reconstructions of a finished game's
/// board that a viewer can watch play out turn-by-turn (paced, pausable, resettable). Mirrors the RL
/// training server's own pattern (Server/Program.cs's `opt.UseInMemoryDatabase("TrainingDB")` for
/// `--training` mode) — a real ApplicationDbContext/controllers, just backed by an EF InMemory provider
/// instead of the production database, so nothing here ever touches a real Game/Player/Unit row. A session
/// is never a real Game.Id — every method here keys off a synthetic replaySessionId instead.
///
/// The replay loop runs as fire-and-forget background work outliving the HTTP request that started it, so
/// (like BotService.TriggerBotTurn) it resolves its own dependencies from a fresh IServiceScopeFactory scope
/// rather than capturing the request-scoped services of whichever controller called StartReplayAsync —
/// those get disposed the moment that HTTP request ends.
/// </summary>
public class ReplaySessionManager : IDisposable
{
    // Starting pace for newly created sessions. Settable (not const) so tests can fast-forward it,
    // mirroring BotService.SkipDelays. A viewer's own speed choice then lives on ReplaySession.PacingMs;
    // this stays the default every session begins at.
    public int PacingMs { get; set; } = Shared.Constants.ReplaySpeed.DefaultPacingMs;

    // The per-step delay is served in slices this long rather than one Task.Delay(PacingMs), so changing
    // speed mid-beat takes effect within a slice instead of after the current (up to 10s) wait finishes.
    private const int PacingSliceMs = 100;

    // How often the loop re-checks a paused session for resumption.
    private const int PauseCheckMs = 200;

    // How long a session may go untouched before it's evicted. StopAsync (the viewer clicking "Exit Replay",
    // or GameRoom.razor's DisposeAsync on navigating away) is only ever best-effort: a closed laptop, a
    // crashed tab or a dropped connection never sends it, and this manager is a Singleton holding a live
    // in-memory ApplicationDbContext per session — so without this sweep those orphans accumulate for the
    // lifetime of the process. Any viewer actually watching keeps their session alive implicitly, since
    // GetReplayState polls roughly every 400ms and Get() refreshes LastAccessedUtc.
    // Settable for the same reason as PacingMs.
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan IdleSweepInterval = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ReplaySessionManager> _logger;
    private readonly ConcurrentDictionary<Guid, ReplaySession> _sessions = new();
    private readonly Timer _idleSweepTimer;
    private bool _disposed;

    public ReplaySessionManager(IServiceScopeFactory scopeFactory, ILogger<ReplaySessionManager> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _idleSweepTimer = new Timer(_ => _ = EvictIdleSessionsAsync(), null, IdleSweepInterval, IdleSweepInterval);
    }

    /// <summary>
    /// Disposes every session whose last access is older than <see cref="IdleTimeout"/>. Runs on a timer,
    /// and is also safe to call directly (tests do). Eviction goes through the same StopAsync path a viewer
    /// would trigger, so the background loop is always cancelled and awaited before its context is disposed.
    /// </summary>
    public async Task<int> EvictIdleSessionsAsync()
    {
        var cutoff = DateTime.UtcNow - IdleTimeout;
        // Snapshot the keys first: StopAsync mutates the dictionary.
        var stale = _sessions.Where(kv => kv.Value.LastAccessedUtc <= cutoff).Select(kv => kv.Key).ToList();

        int evicted = 0;
        foreach (var id in stale)
        {
            try
            {
                // TryRemove inside StopAsync makes this safe against a concurrent sweep or a viewer
                // stopping the same session — only one caller wins and disposes.
                if (await StopAsync(id))
                {
                    evicted++;
                    _logger.LogInformation("[ReplaySession {Id}] Evicted after {Minutes:F0} min idle.", id, IdleTimeout.TotalMinutes);
                }
            }
            catch (Exception ex)
            {
                // One bad session must not stop the sweep from reclaiming the rest.
                _logger.LogError(ex, "[ReplaySession {Id}] Failed to evict idle session.", id);
            }
        }
        return evicted;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _idleSweepTimer.Dispose();
        foreach (var id in _sessions.Keys.ToList())
        {
            try { StopAsync(id).GetAwaiter().GetResult(); } catch { /* best effort on shutdown */ }
        }
        GC.SuppressFinalize(this);
    }

    public ReplaySession? Get(Guid replaySessionId)
    {
        if (_sessions.TryGetValue(replaySessionId, out var session))
        {
            session.LastAccessedUtc = DateTime.UtcNow;
            return session;
        }
        return null;
    }

    // sourceGame.Name is used only for the scratch replay Game's display name; orderedActions must already
    // be sorted by OrderIndex and setupMeta must be the deserialized StartGame action's GameSetupMetadata —
    // the caller (GamesController.StartReplay) already has to load/validate both to authorize the request.
    public async Task<Guid> StartReplayAsync(Game sourceGame, List<GameActionDto> orderedActions, GameSetupMetadata setupMeta)
    {
        var replaySessionId = Guid.NewGuid();
        var replayGameId = Guid.NewGuid();
        var replayContext = CreateInMemoryContext(replaySessionId);

        await SeedGameAsync(replayContext, replayGameId, sourceGame.Name, setupMeta);

        var session = new ReplaySession
        {
            Id = replaySessionId,
            SourceGameId = sourceGame.Id,
            ReplayGameId = replayGameId,
            Context = replayContext,
            Actions = orderedActions,
            PacingMs = PacingMs
        };
        _sessions[replaySessionId] = session;

        session.LoopTask = RunReplayLoopAsync(session);

        return replaySessionId;
    }

    public void Pause(Guid replaySessionId)
    {
        if (_sessions.TryGetValue(replaySessionId, out var session)) session.IsPaused = true;
    }

    public void Resume(Guid replaySessionId)
    {
        if (_sessions.TryGetValue(replaySessionId, out var session)) session.IsPaused = false;
    }

    /// <summary>
    /// Sets a session's playback speed, returning the value actually applied after normalization, or
    /// null when no such session exists. Takes effect on the beat currently being waited out, not just
    /// the next one — see PacingSliceMs.
    /// </summary>
    public int? SetSpeed(Guid replaySessionId, int pacingMs)
    {
        if (!_sessions.TryGetValue(replaySessionId, out var session)) return null;

        session.PacingMs = Shared.Constants.ReplaySpeed.Normalize(pacingMs);
        session.LastAccessedUtc = DateTime.UtcNow;
        return session.PacingMs;
    }

    public async Task<bool> ResetAsync(Guid replaySessionId)
    {
        if (!_sessions.TryGetValue(replaySessionId, out var session)) return false;

        await StopLoopAsync(session);
        session.Context.Dispose();

        var startGameAction = session.Actions.First(a => a.ActionType == "StartGame");
        var setupMeta = JsonSerializer.Deserialize<GameSetupMetadata>(startGameAction.Metadata, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        // A fresh InMemory store name — reusing the old one could resurrect stale data if disposal hasn't
        // fully released it yet.
        session.Context = CreateInMemoryContext(Guid.NewGuid());
        await SeedGameAsync(session.Context, session.ReplayGameId, "Replay", setupMeta);

        // session.PacingMs is deliberately left alone: Reset restarts the playback, not the viewer's
        // chosen speed.
        session.CurrentActionIndex = -1;
        session.IsPaused = false;
        session.IsComplete = false;
        session.ErrorMessage = null;
        session.LatestSnapshot = null;
        session.Cts = new CancellationTokenSource();

        session.LoopTask = RunReplayLoopAsync(session);
        return true;
    }

    public async Task<bool> StopAsync(Guid replaySessionId)
    {
        if (!_sessions.TryRemove(replaySessionId, out var session)) return false;
        await StopLoopAsync(session);
        session.Context.Dispose();
        return true;
    }

    private static ApplicationDbContext CreateInMemoryContext(Guid uniqueSeed)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"Replay-{uniqueSeed}")
            .Options;
        return new ApplicationDbContext(options);
    }

    private static async Task SeedGameAsync(ApplicationDbContext context, Guid gameId, string sourceName, GameSetupMetadata setupMeta)
    {
        context.Games.Add(new Game
        {
            Id = gameId,
            Name = $"{sourceName} (Replay)",
            Status = GameStatus.Lobby,
            MaxPlayers = setupMeta.MaxPlayers > 0 ? setupMeta.MaxPlayers : setupMeta.Players.Count,
            IsPrivate = setupMeta.IsPrivate,
            VariantBonusOnlyForTaxIncreases = setupMeta.VariantBonusOnlyForTaxIncreases
        });
        await context.SaveChangesAsync();
        await GameSetupHelper.ReconstructRosterAndSetupAsync(context, gameId, setupMeta);
    }

    private async Task StopLoopAsync(ReplaySession session)
    {
        session.Cts.Cancel();
        if (session.LoopTask != null)
        {
            try { await session.LoopTask; }
            catch { /* expected: the loop observes cancellation via OperationCanceledException */ }
        }
    }

    private async Task RunReplayLoopAsync(ReplaySession session)
    {
        using var scope = _scopeFactory.CreateScope();
        var hubContext = scope.ServiceProvider.GetRequiredService<IHubContext<GameHub>>();
        var presenceTracker = scope.ServiceProvider.GetRequiredService<PresenceTracker>();
        var botService = scope.ServiceProvider.GetRequiredService<BotService>();
        var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var replayGamesController = new GamesController(session.Context, userManager, hubContext, presenceTracker, botService, notificationService) { SuppressBroadcasts = true };
        var replayManeuverController = new ManeuverController(session.Context, hubContext, botService) { SuppressBroadcasts = true };
        var replayService = new GameReplayService();
        var token = session.Cts.Token;

        try
        {
            var result = await replayService.ReplayActionsAsync(
                session.Context, session.ReplayGameId, replayGamesController, replayManeuverController,
                session.Actions, suppressBroadcasts: true,
                onActionReplayed: async (_, index, wasSkipped) =>
                {
                    token.ThrowIfCancellationRequested();
                    session.CurrentActionIndex = index;

                    // Skipped entries (JoinGame/LeaveGame/StartGame/Investor/InvestorBonus) are purely
                    // informational — GameReplayService applies no state change for them, so pausing a full
                    // beat on each just stalls playback with nothing to look at. They were making the gap
                    // between two consecutive *visible* events run several times PacingMs (e.g. an investor
                    // payout followed by three skipped entries showed as ~20s at the 5s default instead of
                    // one 5s beat). Advance the index so progress still counts them, then move straight on.
                    if (wasSkipped) return;

                    await CaptureSnapshotAsync(session, replayGamesController);
                    await WaitForNextStepAsync(session, token);
                });

            session.IsComplete = true;
            if (!result.Success)
            {
                session.ErrorMessage = $"Replay stopped at action #{result.FailedActionOrderIndex} ({result.FailedActionType}): {result.ErrorMessage}";
                _logger.LogWarning("[ReplaySession {Id}] {Error}", session.Id, session.ErrorMessage);
            }
            else
            {
                await CaptureSnapshotAsync(session, replayGamesController);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: Stop/Reset cancels the token deliberately.
        }
        catch (Exception ex)
        {
            session.ErrorMessage = ex.Message;
            _logger.LogError(ex, "[ReplaySession {Id}] Replay loop failed", session.Id);
        }
    }

    /// <summary>
    /// Waits out one playback beat, then holds while the session is paused.
    ///
    /// The wait is served in <see cref="PacingSliceMs"/> slices re-reading session.PacingMs each time,
    /// rather than a single Task.Delay: a viewer who drops from 10s to 0.5s should see playback speed up
    /// right away instead of sitting through the remainder of a delay that was scheduled at the old
    /// value. Lowering the speed below what has already elapsed simply ends the wait immediately.
    ///
    /// A PacingMs of 0 (what the tests set) skips the loop entirely, preserving the old fast-forward
    /// behaviour exactly.
    /// </summary>
    private static async Task WaitForNextStepAsync(ReplaySession session, CancellationToken token)
    {
        int waited = 0;
        while (waited < session.PacingMs)
        {
            token.ThrowIfCancellationRequested();
            var slice = Math.Min(PacingSliceMs, session.PacingMs - waited);
            await Task.Delay(slice, token);
            waited += slice;
        }

        while (session.IsPaused)
        {
            await Task.Delay(PauseCheckMs, token);
        }
    }

    private static async Task CaptureSnapshotAsync(ReplaySession session, GamesController gamesController)
    {
        var game = await session.Context.Games
            .Include(g => g.Players).ThenInclude(p => p.User)
            .Include(g => g.NationStates).ThenInclude(ns => ns.Controller).ThenInclude(c => c!.User)
            .Include(g => g.Bonds).ThenInclude(b => b.Holder).ThenInclude(h => h!.User)
            .Include(g => g.TerritoryStates)
            .Include(g => g.Units)
            .Include(g => g.Actions)
            .AsSplitQuery()
            .FirstOrDefaultAsync(g => g.Id == session.ReplayGameId);
        if (game != null)
        {
            session.LatestSnapshot = gamesController.BuildGameDetailDto(game, null);
        }
    }
}
