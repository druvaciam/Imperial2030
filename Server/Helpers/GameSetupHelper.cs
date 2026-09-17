using Imperial2030.Server.Data;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Imperial2030.Server.Helpers;

public static class GameSetupHelper
{
    // Shared by ImportGame and ReplaySessionManager: given a StartGame action's GameSetupMetadata (the
    // roster + nation distribution snapshot), creates fresh Player rows for `gameId` (with fresh IDs, since
    // the originals may already exist in this DB or belong to a different server entirely) and runs
    // InitializeGameAsync against them. Kept IsBot = false throughout so BotService never fires a concurrent
    // move against the same game row while GameReplayService is deliberately replaying it (mirrors
    // Tests/ReplayGameTests.cs's established "IsBot = false // Prevent BotService from auto-playing" seeding
    // pattern). Returns the original PlayerId -> fresh Player.Id map, e.g. to remap other data.
    //
    // Every Player needs SOME non-null UserId, because GameReplayService builds an auth Claim from it while
    // replaying (Claim's constructor throws on a null value). What that value can safely be depends on
    // whether `context` is backed by a real relational database:
    //  - `userManager` provided (ImportGame, writing to the real persisted database): Player.UserId is a real
    //    FK to AspNetUsers there, so it needs an ACTUAL row — a synthetic placeholder string alone violates
    //    the FK constraint on any provider that enforces it. (Confirmed against a live SQL Server: EF's
    //    InMemory provider used throughout this feature's automated tests never enforces FK constraints at
    //    all, so this gap was invisible until tested against a real database.) A real, throwaway
    //    ApplicationUser is created per player — never logged into (a random, never-communicated password)
    //    and never surfaced as a real player anywhere (IsBot = true is set by the caller once replay
    //    succeeds, same as before; BotName/UserName is what every UI surface actually displays).
    //  - `userManager` omitted (ReplaySessionManager's purely in-memory sessions): no relational FK exists to
    //    violate, so a plain synthetic GUID string is enough.
    public static async Task<Dictionary<Guid, Guid>> ReconstructRosterAndSetupAsync(ApplicationDbContext context, Guid gameId, GameSetupMetadata setupMeta, UserManager<ApplicationUser>? userManager = null, string? startedBy = null)
    {
        // Fresh IDs (see above for why the originals can't be reused), but handed out so their relative sort
        // order matches the original roster's. PlayerHelper.GetOrderedPlayers() sorts by Player.Id, and that
        // order IS the seating order — it decides the starting investor-card holder (InitializeGameAsync
        // below), who the card passes to each time an investor phase completes (PlayerHelper.GetNextPlayerId)
        // and the order Swiss Bank players invest in. Independently-random GUIDs reseat everyone into a
        // different permutation, so the card lands on the wrong player from the very first investor phase and
        // the replay diverges from history for the rest of the game (observed as an extra "Swiss Bank"
        // investor left pending, which then blocks EndTurn with "Waiting for Investor Phase" hundreds of
        // actions later). GetOrderedPlayers is LINQ-to-Objects, so .NET Guid ordering applies here
        // consistently regardless of which database provider backs `context`.
        var originalIdsInSeatingOrder = setupMeta.Players.Select(p => p.PlayerId).OrderBy(id => id).ToList();
        var freshIdsInSeatingOrder = originalIdsInSeatingOrder.Select(_ => Guid.NewGuid()).OrderBy(id => id).ToList();
        var idMap = originalIdsInSeatingOrder
            .Select((originalId, index) => (originalId, freshId: freshIdsInSeatingOrder[index]))
            .ToDictionary(pair => pair.originalId, pair => pair.freshId);

        foreach (var entry in setupMeta.Players)
        {
            string playerUserId;
            if (userManager != null)
            {
                var throwawayUser = new ApplicationUser
                {
                    UserName = $"import-{idMap[entry.PlayerId]:N}",
                    Email = $"import-{idMap[entry.PlayerId]:N}@imported.local"
                };
                var createResult = await userManager.CreateAsync(throwawayUser, $"Import-{Guid.NewGuid():N}!A1");
                if (!createResult.Succeeded)
                {
                    throw new InvalidOperationException($"Could not create placeholder account for imported player: {string.Join(", ", createResult.Errors.Select(e => e.Description))}");
                }
                playerUserId = throwawayUser.Id;
            }
            else
            {
                playerUserId = idMap[entry.PlayerId].ToString();
            }

            context.Players.Add(new Player
            {
                Id = idMap[entry.PlayerId],
                GameId = gameId,
                UserId = playerUserId,
                IsHost = entry.IsHost,
                IsBot = false,
                BotName = !string.IsNullOrEmpty(entry.DisplayName) ? entry.DisplayName : (entry.BotName ?? "Player"),
                BotType = entry.BotType
            });
        }
        await context.SaveChangesAsync();

        // The set-up logs a StartGame entry of its own, remapped onto the fresh Player IDs/UserIds just
        // created. GameReplayService's skip-list treats the source's "StartGame" as a no-op, so replaying the
        // source's actions never logs one into this game - and without one the game could never itself be
        // the source of a later "Start Replay" or export.
        var mappedDistribution = setupMeta.NationDistribution.ToDictionary(kvp => kvp.Key, kvp => idMap[kvp.Value]);
        var freshPlayers = await context.Players.Where(p => p.GameId == gameId).ToListAsync();
        var remappedRoster = setupMeta.Players.Select(p => new PlayerRosterEntry
        {
            PlayerId = idMap[p.PlayerId],
            UserId = freshPlayers.First(np => np.Id == idMap[p.PlayerId]).UserId,
            IsHost = p.IsHost,
            IsBot = p.IsBot,
            BotName = p.BotName,
            BotType = p.BotType,
            DisplayName = p.DisplayName
        }).ToList();
        await InitializeGameAsync(context, gameId, mappedDistribution, startedBy ?? GameConstants.SystemPlayerName, remappedRoster);
        context.ChangeTracker.Clear();

        return idMap;
    }

    /// <summary>
    /// Loads <paramref name="gameId"/> with its players, sets it up with
    /// <see cref="Engine.GameLifecycle.Start"/> and saves. The deal is random unless
    /// <paramref name="forcedDistribution"/> gives it - the one recorded on a game's <c>StartGame</c> action
    /// reproduces that game's set-up exactly, which is what makes it replayable from its action log alone.
    /// Returns the nation -> player deal that was used.
    /// </summary>
    public static async Task<Dictionary<Nation, Player>> InitializeGameAsync(ApplicationDbContext context, Guid gameId, Dictionary<Nation, Guid>? forcedDistribution = null, string? startedBy = null, List<PlayerRosterEntry>? roster = null)
    {
        var game = await context.LoadGameGraphAsync(gameId) ?? throw new InvalidOperationException($"Game {gameId} not found.");

        var started = Engine.GameLifecycle.Start(context, game, forcedDistribution, startedBy ?? GameConstants.SystemPlayerName, roster);
        if (!started.Ok) throw new InvalidOperationException(started.Error);
        await context.SaveChangesAsync();

        return started.Distribution.ToDictionary(kvp => kvp.Key, kvp => game.Players.First(p => p.Id == kvp.Value));
    }
}
