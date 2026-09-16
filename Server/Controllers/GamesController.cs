using Imperial2030.Server.Data;
using Imperial2030.Server.Engine;
using Imperial2030.Server.Models;
using Imperial2030.Server.Helpers;
using Imperial2030.Shared.Models;
using Imperial2030.Shared.Constants;
using Imperial2030.Server.Helpers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using System;
using System.Linq;
using System.Text.Json;

namespace Imperial2030.Server.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize]
public class GamesController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IHubContext<Imperial2030.Server.Hubs.GameHub> _hubContext;
    private readonly Imperial2030.Server.Services.PresenceTracker _presenceTracker;
    private readonly Imperial2030.Server.Services.BotService _botService;
    private readonly Imperial2030.Server.Services.INotificationService _notificationService;
    private readonly ILogger<GamesController> _logger;
    private readonly Imperial2030.Server.Services.BotTypeCatalog _botTypeCatalog;

    /// <summary>
    /// When true, suppresses all SignalR broadcasts from this controller instance. Set by
    /// GameReplayService while replaying actions (e.g. during ImportGame) so a large replay doesn't
    /// spam every connected browser with GameUpdated/GameStarted/etc. events for a game they can't see yet.
    /// </summary>
    public bool SuppressBroadcasts { get; set; } = false;

    // logger is optional so the many direct `new GamesController(...)` constructions in Tests/ keep
    // working; DI supplies the real one in production.
    public GamesController(ApplicationDbContext context, UserManager<ApplicationUser> userManager, IHubContext<Imperial2030.Server.Hubs.GameHub> hubContext, Imperial2030.Server.Services.PresenceTracker presenceTracker, Imperial2030.Server.Services.BotService botService, Imperial2030.Server.Services.INotificationService notificationService, ILogger<GamesController>? logger = null, Imperial2030.Server.Services.BotTypeCatalog? botTypeCatalog = null)
    {
        _context = context;
        _userManager = userManager;
        _hubContext = hubContext;
        _presenceTracker = presenceTracker;
        _botService = botService;
        _notificationService = notificationService;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GamesController>.Instance;
        _botTypeCatalog = botTypeCatalog ?? new Imperial2030.Server.Services.BotTypeCatalog();
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<ActionResult<IEnumerable<GameDto>>> GetGames()
    {
        var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        return await _context.Games
            .OrderByDescending(g => g.CreatedAt)
            .Select(g => new GameDto
            {
                Id = g.Id,
                Name = g.Name,
                Status = g.Status,
                CreatedAt = g.CreatedAt,
                FinishedAt = g.FinishedAt,
                PlayerCount = g.Players.Count,
                MaxPlayers = g.MaxPlayers,
                IsPrivate = g.IsPrivate,
                VariantBonusOnlyForTaxIncreases = g.VariantBonusOnlyForTaxIncreases,
                JoinCode = g.Players.Any(p => p.UserId == currentUserId && p.IsHost) ? g.JoinCode : null,
                // Null-guarded: an anonymous caller has no id, and bots have a null UserId, so an
                // unguarded comparison would match every bot player.
                IsCurrentUserInGame = currentUserId != null && g.Players.Any(p => p.UserId == currentUserId),
                IsCurrentUserHost = currentUserId != null && g.Players.Any(p => p.IsHost && p.UserId == currentUserId),
                HostName = g.Players.Where(p => p.IsHost).Select(p => p.User.UserName).FirstOrDefault(),
                MaxPower = g.NationStates.Any() ? g.NationStates.Max(ns => ns.Power) : 0,
                TurnCount = g.TurnCount,
                WinnerName = g.WinnerName,
                IsPaused = g.IsPaused,
                IsAllBots = g.Players.Any() && g.Players.All(p => p.IsBot)
            })
            .ToListAsync();
    }

    [HttpPost]
    [Authorize(Policy = GameConstants.NotGuestPolicy)]
    public async Task<ActionResult<GameDto>> CreateGame([FromBody] CreateGameRequest req)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var game = new Game
        {
            Name = req.Name,
            MaxPlayers = req.MaxPlayers,
            IsPrivate = req.IsPrivate,
            JoinCode = req.IsPrivate ? GenerateJoinCode() : null,
            VariantBonusOnlyForTaxIncreases = req.VariantBonusOnlyForTaxIncreases
        };
        _context.Games.Add(game);

        var player = new Player
        {
            GameId = game.Id,
            UserId = userId,
            IsHost = true
        };
        _context.Players.Add(player);

        await _context.SaveChangesAsync();

        var gameDto = new GameDto
        {
            Id = game.Id,
            Name = game.Name,
            Status = game.Status,
            CreatedAt = game.CreatedAt,
            FinishedAt = game.FinishedAt,
            WinnerName = game.WinnerName,
            PlayerCount = 1,
            MaxPlayers = game.MaxPlayers,
            IsPrivate = game.IsPrivate,
            JoinCode = game.JoinCode,
            MaxPower = game.NationStates.Any() ? game.NationStates.Max(ns => ns.Power) : 0,
            TurnCount = game.TurnCount,
            VariantBonusOnlyForTaxIncreases = game.VariantBonusOnlyForTaxIncreases,
            IsPaused = game.IsPaused,
            // The creator is, by construction, the sole player and the host.
            IsCurrentUserInGame = true,
            IsCurrentUserHost = true,
            HostName = User.Identity?.Name
        };

        if (!SuppressBroadcasts) { await _hubContext.Clients.All.SendAsync("GameCreated", gameDto); }

        return CreatedAtAction(nameof(GetGames), new { id = game.Id }, gameDto);
    }

    [HttpPost("{gameId}/join")]
    [Authorize(Policy = GameConstants.NotGuestPolicy)]
    public async Task<IActionResult> JoinGame(Guid gameId, [FromBody] JoinGameRequest? req)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var game = await _context.Games.Include(g => g.Players).FirstOrDefaultAsync(g => g.Id == gameId);
        if (game == null) return NotFound();

        if (game.Status != GameStatus.Lobby)
            return BadRequest("Game has already started or is finished.");

        if (game.Players.Any(p => p.UserId == userId))
            return BadRequest("You are already in this game.");

        if (game.Players.Count >= game.MaxPlayers)
            return BadRequest("Game is full.");

        if (game.IsPrivate)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.JoinCode) || !string.Equals(req.JoinCode, game.JoinCode, StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest("Invalid join code provided for a private game.");
            }
        }

        var player = new Player
        {
            GameId = game.Id,
            UserId = userId,
            IsHost = false
        };
        _context.Players.Add(player);
        GameLogger.LogJoinGame(_context, game, User.Identity?.Name ?? GameConstants.SystemPlayerName);
        await _context.SaveChangesAsync();

        if (!SuppressBroadcasts) { await _hubContext.Clients.All.SendAsync("GameUpdated", gameId); }

        return Ok();
    }

    [HttpPost("{gameId}/leave")]
    [Authorize(Policy = GameConstants.NotGuestPolicy)]
    public async Task<IActionResult> LeaveGame(Guid gameId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var game = await _context.Games
            .Include(g => g.Players)
            .Include(g => g.Bonds)
            .Include(g => g.NationStates)
            .AsSplitQuery()
            .FirstOrDefaultAsync(g => g.Id == gameId);

        if (game == null) return NotFound();

        if (game.Status == GameStatus.Finished || game.Status == GameStatus.InProgress)
        {
            return BadRequest("Cannot leave a game that has already started. It remains in your history.");
        }

        var player = game.Players.FirstOrDefault(p => p.UserId == userId);
        if (player == null) return BadRequest("You are not in this game.");

        // Clear assets (FK constraints)
        foreach (var bond in game.Bonds.Where(b => b.HolderId == player.Id))
        {
            bond.HolderId = null;
            // bond.Holder = null; // EF should handle this via ID
            _context.Entry(bond).State = EntityState.Modified;
        }

        foreach (var ns in game.NationStates.Where(n => n.ControllerId == player.Id))
        {
            ns.ControllerId = null;
            // ns.Controller = null;
            _context.Entry(ns).State = EntityState.Modified;
        }

        // Must save these changes before removing player? 
        // Or EF can figure it out in one transaction if we nullify first.

        // Remove player from the DB context
        _context.Players.Remove(player);
        // Also remove from the in-memory collection so .Any() evaluates correctly
        game.Players.Remove(player);

        bool hasHumanPlayers = game.Players.Any(p => !p.IsBot);

        if (!hasHumanPlayers && game.Status != GameStatus.Finished)
        {
            var fullGame = await _context.Games
                .Include(g => g.TerritoryStates)
                .Include(g => g.Units)
                .AsSplitQuery()
                .FirstOrDefaultAsync(g => g.Id == gameId);

            if (fullGame != null)
            {
                await _context.GameActions.Where(a => a.GameId == gameId).ExecuteDeleteAsync();
                _context.Bonds.RemoveRange(game.Bonds);
                _context.NationStates.RemoveRange(game.NationStates);
                _context.TerritoryStates.RemoveRange(fullGame.TerritoryStates);
                _context.Units.RemoveRange(fullGame.Units);
                _context.Players.RemoveRange(game.Players);
                _context.Games.Remove(fullGame);
                await _context.SaveChangesAsync();
            }
        }
        else
        {
            // If the player was the host, assign a new host if there are other players
            if (player.IsHost)
            {
                var newHost = game.Players.FirstOrDefault(p => !p.IsBot) ?? game.Players.FirstOrDefault();
                if (newHost != null)
                {
                    newHost.IsHost = true;
                    _context.Entry(newHost).State = EntityState.Modified;
                }
            }

            GameLogger.LogLeaveGame(_context, game, User.Identity?.Name ?? GameConstants.SystemPlayerName);
            await _context.SaveChangesAsync();
        }

        if (!SuppressBroadcasts) { await _hubContext.Clients.All.SendAsync("GameUpdated", gameId); }

        return Ok();
    }

    [HttpDelete("{gameId}")]
    [Authorize(Policy = GameConstants.NotGuestPolicy)]
    public async Task<IActionResult> DeleteGame(Guid gameId)
    {

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var game = await _context.Games
            .Include(g => g.Players)
            .Include(g => g.Bonds)
            .Include(g => g.NationStates)
            .Include(g => g.TerritoryStates)
            .Include(g => g.Units)
            .AsSplitQuery()
            .FirstOrDefaultAsync(g => g.Id == gameId);

        if (game == null) return NotFound();

        // Imported (and bot-vs-bot exhibition) games have no real human host — the importer never gets a
        // Player row of their own — so the normal host-only check can never pass for them. Any signed-in,
        // non-guest user may delete an all-bot game instead; a game with any real player still requires
        // being that player and being the host.
        bool isAllBotGame = game.Players.Any() && game.Players.All(p => p.IsBot);
        if (!isAllBotGame)
        {
            var player = game.Players.FirstOrDefault(p => p.UserId == userId);
            if (player == null || !player.IsHost) return Forbid();
        }

        await _context.GameActions.Where(a => a.GameId == gameId).ExecuteDeleteAsync();

        _context.Bonds.RemoveRange(game.Bonds);
        _context.NationStates.RemoveRange(game.NationStates);
        _context.TerritoryStates.RemoveRange(game.TerritoryStates);
        _context.Units.RemoveRange(game.Units);
        _context.Players.RemoveRange(game.Players);

        _context.Games.Remove(game);
        await _context.SaveChangesAsync();

        // Nobody disconnects when a game is deleted, so PresenceTracker's per-connection cleanup would
        // never reach this game's entries and they would sit in the singleton for the process lifetime.
        _presenceTracker.RemoveGame(gameId.ToString());

        if (!SuppressBroadcasts) { await _hubContext.Clients.All.SendAsync("GameDeleted", gameId); }
        if (!SuppressBroadcasts) { await _hubContext.Clients.All.SendAsync("GameUpdated", gameId); }
        return Ok();
    }

    [HttpGet("{gameId}")]
    [AllowAnonymous]
    public async Task<ActionResult<GameDetailDto>> GetGame(Guid gameId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        var game = await _context.Games
            .Include(g => g.Players)
                .ThenInclude(p => p.User)
            .Include(g => g.NationStates) // Maps to DB
                .ThenInclude(ns => ns.Controller) // Include Controller
                    .ThenInclude(c => c.User) // Include User for Name
            .Include(g => g.Bonds)
                .ThenInclude(b => b.Holder)
                    .ThenInclude(h => h.User)
            .Include(g => g.TerritoryStates)
            .Include(g => g.Units)
            .Include(g => g.Actions)
            .AsSplitQuery()
            .FirstOrDefaultAsync(g => g.Id == gameId);

        if (game == null) return NotFound();

        var dto = BuildGameDetailDto(game, userId);

        if (game.Status == GameStatus.InProgress)
        {
            bool botTurn = false;
            if (game.IsInvestorTurn && game.ActingPlayerId.HasValue)
            {
                var actor = game.Players.FirstOrDefault(p => p.Id == game.ActingPlayerId);
                if (actor != null && actor.IsBot) botTurn = true;
            }
            else if (!game.IsInvestorTurn)
            {
                var ns = game.NationStates.FirstOrDefault(n => n.Nation == game.CurrentTurnNation);
                if (ns?.ControllerId != null)
                {
                    var controller = game.Players.FirstOrDefault(p => p.Id == ns.ControllerId);
                    if (controller != null && controller.IsBot) botTurn = true;
                }
            }

            if (!botTurn && game.PendingBattleTerritoryId != null && game.PendingBattleDefenders.Any())
            {
                var botDefenders = game.PendingBattleDefenders.Where(nation =>
                {
                    var ns = game.NationStates.FirstOrDefault(n => n.Nation == nation);
                    if (ns == null || ns.ControllerId == null) return false;
                    var controller = game.Players.FirstOrDefault(p => p.Id == ns.ControllerId);
                    return controller != null && controller.IsBot;
                }).ToList();
                if (botDefenders.Any()) botTurn = true;
            }

            if (!botTurn && game.PendingSwissBankForceNation != null && game.PendingSwissBankResponders.Any())
            {
                var botResponders = game.PendingSwissBankResponders.Select(id => game.Players.FirstOrDefault(p => p.Id == id)).Where(p => p != null && p.IsBot).ToList();
                if (botResponders.Any()) botTurn = true;
            }

            if (botTurn)
            {
                _botService.TriggerBotTurn(gameId);
            }
        }

        return dto;
    }

    // Extracted from GetGame so ReplayState (Server/Services/ReplaySessionManager.cs's in-memory replay
    // sessions) can build the exact same DTO shape from a fully-loaded Game without duplicating this
    // projection. Deliberately excludes GetGame's post-build bot-turn-triggering side effect below it —
    // that only makes sense for real, persisted games, not a scratch in-memory replay session.
    // Internal (not private) so ReplaySessionManager's background replay loop can build the same DTO shape
    // from its own in-memory-context-backed GamesController instance without duplicating this projection.
    internal GameDetailDto BuildGameDetailDto(Game game, string? userId)
    {
        return new GameDetailDto
        {
            Id = game.Id,
            Name = game.Name,
            Status = game.Status,
            CreatedAt = game.CreatedAt,
            FinishedAt = game.FinishedAt,
            WinnerName = game.WinnerName,
            IsPrivate = game.IsPrivate,
            IsPaused = game.IsPaused,
            VariantBonusOnlyForTaxIncreases = game.VariantBonusOnlyForTaxIncreases,
            IsCurrentUserInGame = userId != null && game.Players.Any(p => p.UserId == userId),
            IsCurrentUserHost = userId != null && game.Players.Any(p => p.IsHost && p.UserId == userId),
            JoinCode = game.Players.Any(p => p.UserId == userId && p.IsHost) ? game.JoinCode : null,
            CurrentTurnNation = game.CurrentTurnNation,
            PlayerCount = game.Players.Count,
            Players = game.Players.Select(p => new PlayerDto
            {
                Id = p.Id,
                UserId = p.IsBot ? $"bot-{p.Id}" : p.UserId!,
                // GetPlayerName checks BotName before IsBot: replay/import players are kept IsBot=false with
                // no backing ApplicationUser, and their display name is in BotName (see PlayerHelper).
                UserName = p.GetPlayerName(_context),
                IsHost = p.IsHost,
                Cash = p.Cash,
                IsBot = p.IsBot,
                IsOnline = p.IsBot ? true : _presenceTracker.IsUserOnline(p.UserId),
                IsActiveInGame = p.IsBot ? true : _presenceTracker.IsUserActiveInGame(game.Id.ToString(), p.UserId),
                Bonds = game.Bonds.Where(b => b.HolderId == p.Id).Select(b => new BondDto
                {
                    Id = b.Id,
                    Nation = b.Nation,
                    Cost = b.Cost,
                    Interest = b.Interest,
                    HolderName = p.GetPlayerName(_context)
                }).ToList()
            }).ToList(),
            NationStates = game.NationStates.Select(ns => new NationStateDto
            {
                Nation = ns.Nation,
                Treasury = ns.Treasury,
                Power = ns.Power,
                RondelPosition = ns.RondelPosition,
                // Same as UserName above. ControllerName must not be null during replay: the client's
                // IsMyTurn() compares it with MyPlayer?.UserName, which is null for the replay viewer, and
                // null == null would show action controls during playback.
                ControllerName = ns.Controller != null ? ns.Controller.GetPlayerName(_context) : null,
                ControllerId = ns.ControllerId,
                HasBuiltThisTurn = ns.HasBuiltThisTurn,
                HasProducedThisTurn = ns.HasProducedThisTurn,
                HasMovedThisTurn = ns.HasMovedThisTurn,
                HasImportedThisTurn = ns.HasImportedThisTurn,
                TaxRevenue = ns.TaxRevenue,
                PreviousTaxRevenue = ns.PreviousTaxRevenue
            }).ToList(),
            AvailableBonds = game.Bonds.Where(b => b.HolderId == null).Select(b => new BondDto
            {
                Id = b.Id,
                Nation = b.Nation,
                Cost = b.Cost,
                Interest = b.Interest,
                HolderName = null
            }).ToList(),
            Territories = game.TerritoryStates.Select(ts => new TerritoryStateDto
            {
                TerritoryId = ts.TerritoryId,
                HasFactory = ts.HasFactory,
                Controller = ts.Controller
            }).ToList(),
            InvestorCardHolderId = game.InvestorCardHolderId,
            IsInvestorTurn = game.IsInvestorTurn,
            ActingPlayerId = game.ActingPlayerId,
            PendingBattleTerritoryId = game.PendingBattleTerritoryId,
            PendingBattleAggressorNation = game.PendingBattleAggressorNation,
            PendingBattleDefenders = game.PendingBattleDefenders.ToList(),
            PendingSwissBankForceNation = game.PendingSwissBankForceNation,
            PendingSwissBankResponders = game.PendingSwissBankResponders.ToList(),
            Units = game.Units.ToList(),
            ManeuverState = new ManeuverState { Phase = game.CurrentManeuverPhase },
            Actions = game.Actions.OrderBy(a => a.OrderIndex).ThenBy(a => a.Timestamp).Select(a => new GameActionDto
            {
                Id = a.Id,
                OrderIndex = a.OrderIndex,
                Timestamp = a.Timestamp,
                PlayerName = a.PlayerName,
                Nation = a.Nation,
                ActionType = a.ActionType,
                Message = a.Message,
                Metadata = a.Metadata ?? string.Empty
            }).ToList()
        };
    }

    [HttpGet("{gameId}/export")]
    [AllowAnonymous]
    public async Task<IActionResult> ExportGame(Guid gameId)
    {
        var game = await _context.Games.Include(g => g.Actions).AsSplitQuery().FirstOrDefaultAsync(g => g.Id == gameId);
        if (game == null) return NotFound();
        if (game.Status != GameStatus.Finished) return BadRequest("Only finished games can be exported.");

        var export = new GameExportDto
        {
            FormatVersion = 1,
            OriginalGameId = game.Id,
            OriginalGameName = game.Name,
            ExportedAt = DateTime.UtcNow,
            Actions = game.Actions.OrderBy(a => a.OrderIndex).ThenBy(a => a.Timestamp).Select(a => new GameActionDto
            {
                Id = a.Id,
                OrderIndex = a.OrderIndex,
                Timestamp = a.Timestamp,
                PlayerName = a.PlayerName,
                Nation = a.Nation,
                ActionType = a.ActionType,
                Message = a.Message,
                Metadata = a.Metadata ?? string.Empty
            }).ToList()
        };

        var json = JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true });
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        var safeName = string.Join("_", game.Name.Split(Path.GetInvalidFileNameChars()));
        return File(bytes, "application/json", $"{safeName}_{game.Id}.json");
    }

    // Never stacks a repeat " (Imported)" suffix (a game can be exported and re-imported any number of
    // times — export doesn't care whether its source was itself an import) and always fits within
    // Game.Name's [MaxLength(50)], truncating the original name rather than the suffix so the result stays
    // recognizable as an import. Without this, each import/export cycle grew the name by " (Imported)"
    // until it overflowed the column and every subsequent import of that lineage failed outright.
    private static string BuildImportedGameName(string? originalGameName)
    {
        const string suffix = " (Imported)";
        const int maxNameLength = GameConstants.MaxGameNameLength;
        string baseName = string.IsNullOrWhiteSpace(originalGameName) ? "Imported Game" : originalGameName.Trim();
        if (baseName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return baseName.Length > maxNameLength ? baseName[..maxNameLength] : baseName;
        }
        string name = baseName + suffix;
        if (name.Length <= maxNameLength) return name;
        int keep = Math.Max(0, maxNameLength - suffix.Length);
        return baseName[..Math.Min(keep, baseName.Length)] + suffix;
    }

    [HttpPost("import")]
    [Authorize(Policy = GameConstants.NotGuestPolicy)]
    public async Task<ActionResult<GameDto>> ImportGame([FromBody] GameExportDto import)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        if (import?.Actions == null || import.Actions.Count == 0) return BadRequest("Import file has no actions.");

        var orderedActions = import.Actions.OrderBy(a => a.OrderIndex).ToList();
        var startGameAction = orderedActions.FirstOrDefault(a => a.ActionType == "StartGame");
        if (startGameAction == null || string.IsNullOrEmpty(startGameAction.Metadata))
        {
            return BadRequest("Import file is missing its StartGame roster/setup metadata (exported from an older server version?).");
        }

        GameSetupMetadata? setupMeta;
        try
        {
            setupMeta = JsonSerializer.Deserialize<GameSetupMetadata>(startGameAction.Metadata, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return BadRequest("Could not parse the StartGame action's metadata.");
        }
        if (setupMeta == null || setupMeta.Players.Count < 2 || setupMeta.NationDistribution.Count == 0)
        {
            return BadRequest("Import file's roster/nation-distribution snapshot is missing or incomplete.");
        }

        var rosterIds = setupMeta.Players.Select(p => p.PlayerId).ToHashSet();
        if (setupMeta.NationDistribution.Values.Any(pid => !rosterIds.Contains(pid)))
        {
            return BadRequest("Nation distribution references a player not present in the roster snapshot.");
        }

        var newGameId = Guid.NewGuid();

        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction = null;
        if (_context.Database.IsRelational())
        {
            transaction = await _context.Database.BeginTransactionAsync();
        }

        try
        {
            var newGame = new Game
            {
                Id = newGameId,
                Name = BuildImportedGameName(import.OriginalGameName),
                Status = GameStatus.Lobby,
                MaxPlayers = setupMeta.MaxPlayers > 0 ? setupMeta.MaxPlayers : setupMeta.Players.Count,
                IsPrivate = setupMeta.IsPrivate,
                VariantBonusOnlyForTaxIncreases = setupMeta.VariantBonusOnlyForTaxIncreases
            };
            _context.Games.Add(newGame);
            await _context.SaveChangesAsync();

            // Kept non-bot for the duration of replay below — see GameSetupHelper.ReconstructRosterAndSetupAsync
            // for why, and for why each player gets a real, throwaway ApplicationUser (never logged into) as
            // its UserId rather than a bare placeholder string. Flipped to a real, non-interactive bot once
            // replay succeeds; UserId is left pointing at that throwaway account permanently (satisfying the
            // FK — nothing ever surfaces it as a real player since every UI surface displays BotName instead).
            var importIdMap = await GameSetupHelper.ReconstructRosterAndSetupAsync(_context, newGameId, setupMeta, _userManager);

            // "StartGame" is in GameReplayService's skip-list (it's a no-op consequence during replay, not a
            // live action to re-execute), so the replay below never logs one into THIS game's own action log.
            // Without it, the imported game could never itself be the source of a later "Start Replay" —
            // StartReplay requires a StartGame action's roster/setup snapshot to reconstruct anything. Log one
            // explicitly here, remapped onto the fresh Player IDs/UserIds this import just created, so the
            // imported game is exactly as replayable as any originally-played one.
            var remappedRoster = setupMeta.Players.Select(p =>
            {
                var newPlayer = _context.Players.Local.FirstOrDefault(np => np.Id == importIdMap[p.PlayerId])
                    ?? _context.Players.First(np => np.Id == importIdMap[p.PlayerId]);
                return new PlayerRosterEntry
                {
                    PlayerId = importIdMap[p.PlayerId],
                    UserId = newPlayer.UserId,
                    IsHost = p.IsHost,
                    IsBot = p.IsBot,
                    BotName = p.BotName,
                    BotType = p.BotType,
                    DisplayName = p.DisplayName
                };
            }).ToList();
            var remappedDistribution = setupMeta.NationDistribution.ToDictionary(kvp => kvp.Key, kvp => importIdMap[kvp.Value]);
            GameLogger.LogStartGame(_context, newGame, User.Identity?.Name ?? GameConstants.SystemPlayerName, remappedDistribution, remappedRoster);
            await _context.SaveChangesAsync();

            var replayGamesController = new GamesController(_context, _userManager, _hubContext, _presenceTracker, _botService, _notificationService) { SuppressBroadcasts = true };
            var replayManeuverController = new ManeuverController(_context, _hubContext, _botService) { SuppressBroadcasts = true };
            var replayService = new Imperial2030.Server.Services.GameReplayService();
            var replayResult = await replayService.ReplayActionsAsync(_context, newGameId, replayGamesController, replayManeuverController, orderedActions, suppressBroadcasts: true);

            if (!replayResult.Success)
            {
                if (transaction != null) { await transaction.RollbackAsync(); }
                return BadRequest($"Import failed while replaying action #{replayResult.FailedActionOrderIndex} ({replayResult.FailedActionType}): {replayResult.ErrorMessage}");
            }

            // Replay succeeded: the roster can now safely become non-interactive bots (no more replay in
            // flight for BotService to race against) and the game should already be Finished as a natural
            // consequence of replaying a source game whose own action log ended in a finished state.
            var importedPlayers = await _context.Players.Where(p => p.GameId == newGameId).ToListAsync();
            foreach (var p in importedPlayers)
            {
                p.IsBot = true;
            }
            var finalGame = await _context.Games.FirstAsync(g => g.Id == newGameId);
            if (finalGame.Status != GameStatus.Finished)
            {
                return BadRequest($"Import replayed successfully but the resulting game is '{finalGame.Status}', not Finished — the source export may be incomplete.");
            }
            // WinnerName was already computed and saved mid-replay (inside the replayed Taxation/EndGame
            // action, via GameHelper.SetWinnerNameAsync -> PlayerHelper.GetPlayerName) while every player was
            // still IsBot = false to keep BotService from racing the replay — GetPlayerName's non-bot branch
            // falls back to the throwaway ApplicationUser's UserName in that state, not the intended BotName.
            // Recompute now that IsBot is correctly true for the whole roster.
            await finalGame.SetWinnerNameAsync(_context);
            await _context.SaveChangesAsync();

            if (transaction != null) { await transaction.CommitAsync(); }

            var dto = new GameDto
            {
                Id = finalGame.Id,
                Name = finalGame.Name,
                Status = finalGame.Status,
                CreatedAt = finalGame.CreatedAt,
                FinishedAt = finalGame.FinishedAt,
                WinnerName = finalGame.WinnerName,
                PlayerCount = importedPlayers.Count,
                MaxPlayers = finalGame.MaxPlayers,
                IsPrivate = finalGame.IsPrivate,
                VariantBonusOnlyForTaxIncreases = finalGame.VariantBonusOnlyForTaxIncreases,
                IsPaused = finalGame.IsPaused,
                // An imported game is all bots; the importer never becomes a player in it.
                IsCurrentUserInGame = false,
                IsCurrentUserHost = false,
                HostName = importedPlayers.FirstOrDefault(p => p.IsHost)?.BotName,
                IsAllBots = true
            };
            return Ok(dto);
        }
        catch (Exception ex)
        {
            if (transaction != null) { await transaction.RollbackAsync(); }
            _logger.LogError(ex, "ImportGame failed");
            return StatusCode(500, ErrorResponses.Internal(HttpContext?.TraceIdentifier));
        }
    }

    // --- "Start Replay": paced, in-memory playback of any finished game's own action log (Server/Services/
    // ReplaySessionManager.cs). Never touches this game's real rows — only ever reads its Actions. ---

    [HttpPost("{gameId}/replay/start")]
    [AllowAnonymous]
    // ReplaySessionManager's caps bound how many sessions one caller may HOLD; this bounds how fast they
    // can churn them. Without it, start/stop/start stays under the cap forever while costing a full
    // source-game load and reseed on every cycle.
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(Imperial2030.Server.Configuration.RateLimitPolicies.Replay)]
    public async Task<IActionResult> StartReplay(Guid gameId, [FromServices] Imperial2030.Server.Services.ReplaySessionManager replaySessionManager)
    {
        // Capacity is decided FIRST, before the source game and its entire action log are loaded and
        // projected into DTOs below. This endpoint is [AllowAnonymous], so if admission were checked after
        // that work, every rejected request would still cost a multi-collection query and thousands of
        // allocations — the cap would protect memory while leaving the database open to the same flood.
        var ownerKey = ReplayOwnerKey();
        var admission = replaySessionManager.CheckAdmission(ownerKey);
        if (admission != Imperial2030.Server.Services.ReplayAdmission.Accepted)
        {
            return ReplayCapacityResponse(admission);
        }

        var sourceGame = await _context.Games.Include(g => g.Actions).AsSplitQuery().FirstOrDefaultAsync(g => g.Id == gameId);
        if (sourceGame == null) return NotFound();
        if (sourceGame.Status != GameStatus.Finished) return BadRequest("Only finished games can be replayed.");

        var orderedActions = sourceGame.Actions.OrderBy(a => a.OrderIndex).ThenBy(a => a.Timestamp).Select(a => new GameActionDto
        {
            Id = a.Id,
            OrderIndex = a.OrderIndex,
            Timestamp = a.Timestamp,
            PlayerName = a.PlayerName,
            Nation = a.Nation,
            ActionType = a.ActionType,
            Message = a.Message,
            Metadata = a.Metadata ?? string.Empty
        }).ToList();

        var startGameAction = orderedActions.FirstOrDefault(a => a.ActionType == "StartGame");
        if (startGameAction == null || string.IsNullOrEmpty(startGameAction.Metadata))
        {
            return BadRequest("This game predates the roster/setup snapshot needed for replay.");
        }

        GameSetupMetadata? setupMeta;
        try
        {
            setupMeta = JsonSerializer.Deserialize<GameSetupMetadata>(startGameAction.Metadata, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return BadRequest("Could not parse the StartGame action's metadata.");
        }
        if (setupMeta == null || setupMeta.Players.Count < 2 || setupMeta.NationDistribution.Count == 0)
        {
            return BadRequest("Roster/nation-distribution snapshot is missing or incomplete.");
        }

        // Re-checked inside StartReplayAsync under its admission lock — the check above is only an early-out,
        // and capacity can legitimately fill between the two.
        var start = await replaySessionManager.StartReplayAsync(sourceGame, orderedActions, setupMeta, ownerKey);
        if (start.Admission != Imperial2030.Server.Services.ReplayAdmission.Accepted || start.SessionId == null)
        {
            return ReplayCapacityResponse(start.Admission);
        }

        return Ok(start.SessionId.Value);
    }

    /// <summary>
    /// Identifies a caller for per-caller replay capacity. Uses the transport-level remote address and
    /// deliberately ignores X-Forwarded-For, which a client can set freely — honouring it would hand an
    /// attacker a fresh budget per request. Same trade-off and the same reverse-proxy caveat as the auth
    /// rate limiter; see AuthSecurity.
    /// </summary>
    private string ReplayOwnerKey() =>
        ResolveReplayOwnerKey(User, HttpContext?.Connection.RemoteIpAddress?.ToString());

    /// <summary>
    /// Identifies a caller for per-caller replay capacity.
    ///
    /// Prefers the authenticated identity, falling back to the transport-level remote address only when
    /// there is none. Keying on the address alone was wrong in deployment: behind a reverse proxy that
    /// does not rewrite the connection address (nginx on the VPS), every caller collapses into ONE owner
    /// and shares a single five-session budget — a signed-in user was refused with "You already have the
    /// maximum number of replay sessions open" because unrelated traffic had consumed it.
    ///
    /// An identity is safe to trust here in a way a header is not: the server minted and signature-checked
    /// the token it came from, whereas X-Forwarded-For is attacker-controlled and honouring it would hand
    /// out a fresh budget per request. Guests carry a token too, so they are keyed per guest rather than
    /// lumped together.
    ///
    /// Genuinely anonymous callers (the Vue viewer, which sends no token) still share one bucket per
    /// address, and therefore one bucket in total behind a proxy. That is deliberate: an unauthenticated
    /// flood is precisely what the per-caller cap exists to blunt, and ReplaySessionManager's global
    /// MaxConcurrentSessions is the backstop that actually protects the process. The prefixes keep the two
    /// namespaces distinct so a user id shaped like an address cannot land in that address's bucket.
    /// </summary>
    internal static string ResolveReplayOwnerKey(System.Security.Claims.ClaimsPrincipal? user, string? remoteAddress)
    {
        var userId = user?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!string.IsNullOrEmpty(userId)) return $"user:{userId}";

        return $"ip:{remoteAddress ?? "unknown"}";
    }

    private ObjectResult ReplayCapacityResponse(Imperial2030.Server.Services.ReplayAdmission admission)
    {
        string message = admission == Imperial2030.Server.Services.ReplayAdmission.CallerAtCapacity
            ? "You already have the maximum number of replay sessions open. Close one and try again."
            : "The server is running its maximum number of replay sessions. Please try again shortly.";

        return StatusCode(StatusCodes.Status429TooManyRequests, message);
    }

    [HttpGet("replay/{replaySessionId}")]
    [AllowAnonymous]
    public ActionResult<ReplayStateDto> GetReplayState(Guid replaySessionId, [FromServices] Imperial2030.Server.Services.ReplaySessionManager replaySessionManager)
    {
        var session = replaySessionManager.Get(replaySessionId);
        if (session == null) return NotFound();

        return Ok(new ReplayStateDto
        {
            ReplaySessionId = session.Id,
            SourceGameId = session.SourceGameId,
            CurrentActionIndex = session.CurrentActionIndex,
            TotalActions = session.Actions.Count,
            IsPaused = session.IsPaused,
            PacingMs = session.PacingMs,
            IsComplete = session.IsComplete,
            ErrorMessage = session.ErrorMessage,
            Game = session.LatestSnapshot
        });
    }

    /// <summary>
    /// Sets the playback speed (delay between actions) for one replay session. The requested value is
    /// normalized server-side onto the allowed range/step, and the applied value is returned so the
    /// client shows what actually took effect rather than what it asked for.
    /// </summary>
    [HttpPost("replay/{replaySessionId}/speed")]
    [AllowAnonymous]
    public IActionResult SetReplaySpeed(Guid replaySessionId, [FromQuery] int pacingMs, [FromServices] Imperial2030.Server.Services.ReplaySessionManager replaySessionManager)
    {
        var applied = replaySessionManager.SetSpeed(replaySessionId, pacingMs);
        if (applied == null) return NotFound();
        return Ok(new { PacingMs = applied.Value });
    }

    [HttpPost("replay/{replaySessionId}/pause")]
    [AllowAnonymous]
    public IActionResult PauseReplay(Guid replaySessionId, [FromServices] Imperial2030.Server.Services.ReplaySessionManager replaySessionManager)
    {
        if (replaySessionManager.Get(replaySessionId) == null) return NotFound();
        replaySessionManager.Pause(replaySessionId);
        return Ok();
    }

    [HttpPost("replay/{replaySessionId}/resume")]
    [AllowAnonymous]
    public IActionResult ResumeReplay(Guid replaySessionId, [FromServices] Imperial2030.Server.Services.ReplaySessionManager replaySessionManager)
    {
        if (replaySessionManager.Get(replaySessionId) == null) return NotFound();
        replaySessionManager.Resume(replaySessionId);
        return Ok();
    }

    [HttpPost("replay/{replaySessionId}/reset")]
    [AllowAnonymous]
    public async Task<IActionResult> ResetReplay(Guid replaySessionId, [FromServices] Imperial2030.Server.Services.ReplaySessionManager replaySessionManager)
    {
        var reset = await replaySessionManager.ResetAsync(replaySessionId);
        if (!reset) return NotFound();
        return Ok();
    }

    [HttpPost("replay/{replaySessionId}/stop")]
    [AllowAnonymous]
    public async Task<IActionResult> StopReplay(Guid replaySessionId, [FromServices] Imperial2030.Server.Services.ReplaySessionManager replaySessionManager)
    {
        var stopped = await replaySessionManager.StopAsync(replaySessionId);
        if (!stopped) return NotFound();
        return Ok();
    }

    private static readonly string[] BotNames = { "Bot Alpha", "Bot Bravo", "Bot Charlie", "Bot Delta", "Bot Echo", "Bot Foxtrot" };

    [HttpGet("available-bots")]
    [AllowAnonymous]
    public IActionResult GetAvailableBots()
    {
        var bots = GetAvailableBotTypes();
        return Ok(bots);
    }

    // Discovered once by BotTypeCatalog rather than scanning the deployment directory per call.
    private IReadOnlyList<string> GetAvailableBotTypes() => _botTypeCatalog.Available;

    [HttpPost("{gameId}/add-bot")]
    [Authorize(Policy = GameConstants.NotGuestPolicy)]
    public async Task<IActionResult> AddBot(Guid gameId, [FromQuery] string? botType = null)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var game = await _context.Games.Include(g => g.Players).FirstOrDefaultAsync(g => g.Id == gameId);
        if (game == null) return NotFound();
        if (game.Status != GameStatus.Lobby) return BadRequest("Game must be in lobby.");

        var host = game.Players.FirstOrDefault(p => p.UserId == userId);
        if (host == null || !host.IsHost) return Forbid();
        if (game.Players.Count >= game.MaxPlayers) return BadRequest("Game is full.");

        var existingNames = game.Players.Where(p => p.IsBot).Select(p => p.BotName ?? "").ToList();
        string botName = $"Bot {game.Players.Count + 1}";
        foreach (var name in BotNames)
        {
            if (!existingNames.Any(en => en.StartsWith(name)))
            {
                botName = name;
                break;
            }
        }

        var botTypes = GetAvailableBotTypes();
        var randomBotType = botTypes[Random.Shared.Next(botTypes.Count)];
        var selectedBotType = string.IsNullOrEmpty(botType) || !botTypes.Contains(botType) ? randomBotType : botType;

        var bot = new Player
        {
            UserId = null,
            GameId = gameId,
            IsHost = false,
            IsBot = true,
            BotName = botName + $" ({selectedBotType})",
            BotType = selectedBotType
        };

        _context.Players.Add(bot);
        await _context.SaveChangesAsync();
        if (!SuppressBroadcasts) { await _hubContext.Clients.All.SendAsync("GameUpdated", gameId); }
        return Ok();
    }

    [HttpPost("{gameId}/remove-bot/{playerId}")]
    [Authorize(Policy = GameConstants.NotGuestPolicy)]
    public async Task<IActionResult> RemoveBot(Guid gameId, Guid playerId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var game = await _context.Games.Include(g => g.Players).FirstOrDefaultAsync(g => g.Id == gameId);
        if (game == null) return NotFound();
        if (game.Status != GameStatus.Lobby) return BadRequest("Game must be in lobby.");

        var host = game.Players.FirstOrDefault(p => p.UserId == userId);
        if (host == null || !host.IsHost) return Forbid();

        var bot = game.Players.FirstOrDefault(p => p.Id == playerId && p.IsBot);
        if (bot == null) return NotFound("Bot not found.");

        _context.Players.Remove(bot);
        await _context.SaveChangesAsync();
        if (!SuppressBroadcasts) { await _hubContext.Clients.All.SendAsync("GameUpdated", gameId); }
        return Ok();
    }

    [HttpPost("{gameId}/start")]
    [Authorize(Policy = GameConstants.NotGuestPolicy)]
    public async Task<IActionResult> StartGame(Guid gameId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var gameCheck = await _context.Games.Include(g => g.Players).FirstOrDefaultAsync(g => g.Id == gameId);
        if (gameCheck == null) return NotFound();
        var playerCheck = gameCheck.Players.FirstOrDefault(p => p.UserId == userId);
        if (playerCheck == null) return BadRequest("You are not in this game.");
        if (!playerCheck.IsHost) return Forbid();
        if (gameCheck.Players.Count < 2) return BadRequest("Need at least 2 players to start.");
        if (gameCheck.Status != GameStatus.Lobby) return BadRequest("Game is not in lobby state.");

        // Atomically transition the game out of Lobby state to prevent concurrent StartGame requests
        if (_context.Database.IsRelational())
        {
            var rowsUpdated = await _context.Games
                .Where(g => g.Id == gameId && g.Status == GameStatus.Lobby)
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.Status, GameStatus.InProgress));

            if (rowsUpdated == 0) return BadRequest("Game is already starting or not in lobby state.");
        }
        else
        {
            gameCheck.Status = GameStatus.InProgress;
            await _context.SaveChangesAsync();
        }

        // Ensure the tracked entity knows about this change for later saves
        gameCheck.Status = GameStatus.InProgress;
        gameCheck.StartedAt = DateTime.UtcNow;

        try
        {
            // --- Initialization Logic (Official Imperial 2030 Rules) ---
            // Deals starting bond packages, assigns nation controllers, the investor card holder, and starting
            // cash. The nation->player distribution is randomized here and returned so it can be logged on the
            // StartGame action — that's what lets a game be reproduced later from its action log alone.
            var distribution = await GameSetupHelper.InitializeGameAsync(_context, gameId);

            var startedGame = await _context.Games.Include(g => g.NationStates).FirstOrDefaultAsync(g => g.Id == gameId);
            if (startedGame != null)
            {
                // Fire notification after starting the game
                _ = _notificationService.NotifyGameStartedAsync(startedGame);

                var nationDistribution = distribution.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Id);
                var rosterSnapshot = gameCheck.Players.Select(p => new PlayerRosterEntry
                {
                    PlayerId = p.Id,
                    UserId = p.UserId,
                    IsHost = p.IsHost,
                    IsBot = p.IsBot,
                    BotName = p.BotName,
                    BotType = p.BotType,
                    DisplayName = p.GetPlayerName(_context)
                }).ToList();
                GameLogger.LogStartGame(_context, startedGame, User.Identity?.Name ?? GameConstants.SystemPlayerName, nationDistribution, rosterSnapshot);
                await _context.SaveChangesAsync();
            }

            if (!SuppressBroadcasts) { await _hubContext.Clients.All.SendAsync("GameUpdated", gameId); }
            if (!SuppressBroadcasts) { await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameStarted", gameId); }

            // Trigger bot if first nation is bot-controlled
            _botService.TriggerBotTurn(gameId);

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StartGame failed for game {GameId}", gameId);
            return StatusCode(500, ErrorResponses.Internal(HttpContext?.TraceIdentifier));
        }
    }

    private string GenerateJoinCode()
    {
        return JoinCodeGenerator.Generate();
    }

    [HttpPost("{gameId}/move/{nation}/{targetSlot}")]
    public async Task<IActionResult> MoveNation(Guid gameId, Nation nation, int targetSlot)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var game = await _context.Games
            .Include(g => g.NationStates)
            .Include(g => g.Players)
            .Include(g => g.Bonds)
            .Include(g => g.Units)
            // Landing on Maneuver with nothing of a kind to move ends that phase at once, and a phase end
            // places flags - which reads and writes TerritoryStates. Left unloaded, the pass sees an empty
            // collection and creates a second state row for every occupied region.
            .Include(g => g.TerritoryStates)
            .AsSplitQuery()
            .FirstOrDefaultAsync(g => g.Id == gameId);

        if (game == null) return NotFound();

        // The caller must be the nation's government - checked before the engine runs, because it mutates.
        var nationState = game.NationStates.FirstOrDefault(n => n.Nation == nation);
        if (nationState == null || nationState.ControllerId == null) return BadRequest("No controller for this nation.");
        var controller = game.Players.First(p => p.Id == nationState.ControllerId);
        if (controller.UserId != userId) return Forbid();

        var result = RondelEngine.MoveNation(_context, game, nation, targetSlot);
        if (!result.Ok) return BadRequest(result.Error);

        if (result.SwissBankForcedStop)
        {
            await _context.SaveChangesAsync();
            if (!SuppressBroadcasts) { await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId); }
            _botService.TriggerBotTurn(gameId);
            return Ok();
        }

        // A Maneuver landing with nothing of a kind to move ends that phase at once (p.10, step 3 for the
        // flags). The bot walks its units first and ends the phases itself; here each move does.
        ManeuverEngine.TryAutoAdvanceManeuver(_context, game, nation);
        await _context.SaveChangesAsync();

        if (!SuppressBroadcasts) { await _hubContext.Clients.All.SendAsync("GameUpdated", gameId); }

        // Trigger bot if Investor Phase was activated for a bot
        if (game.IsInvestorTurn)
        {
            _botService.TriggerBotTurn(gameId);
        }

        return Ok();
    }


    [HttpPost("{gameId}/production")]
    public async Task<IActionResult> ExecuteProduction(Guid gameId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var game = await _context.Games
            .Include(g => g.NationStates)
            .Include(g => g.TerritoryStates)
            .Include(g => g.Units)
            .Include(g => g.Players)
            .AsSplitQuery()
            .FirstOrDefaultAsync(g => g.Id == gameId);

        if (game == null) return NotFound();

        // Controller Check - the caller must be the acting nation's government, before the engine mutates.
        var nationState = game.NationStates.First(n => n.Nation == game.CurrentTurnNation);
        if (nationState.ControllerId == null) return BadRequest("No controller.");
        var controller = game.Players.First(p => p.Id == nationState.ControllerId);
        if (controller.UserId != userId) return Forbid();

        var result = ProductionEngine.ExecuteProduction(_context, game);
        if (!result.Ok) return BadRequest(result.Error);

        await _context.SaveChangesAsync();
        if (!SuppressBroadcasts) { await _hubContext.Clients.All.SendAsync("GameUpdated", gameId); }

        return result.ProducedCount > 0
            ? Ok($"Produced {result.ProducedCount} units.")
            : Ok("No units produced (all factories blockaded or none exist).");
    }

    [HttpPost("{gameId}/investor-action")]
    public async Task<IActionResult> PerformInvestment(Guid gameId, [FromBody] InvestmentActionDto action)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var game = await _context.Games
            .Include(g => g.Bonds)
            .Include(g => g.NationStates)
            .Include(g => g.Players)
            .AsSplitQuery().FirstOrDefaultAsync(g => g.Id == gameId);

        if (game == null) return NotFound();
        if (!game.IsInvestorTurn) return BadRequest("Not investor turn.");
        if (game.ActingPlayerId == null) return BadRequest("No acting player.");

        // The caller must be the acting investor - checked before the engine runs, because it mutates.
        var actingPlayer = game.Players.FirstOrDefault(p => p.Id == game.ActingPlayerId);
        if (actingPlayer == null || actingPlayer.UserId != userId) return Forbid();

        if (action.ActionType == "Buy")
        {
            if (action.BondId == null) return BadRequest("BondId required.");

            var bought = InvestorEngine.Buy(_context, game, action.BondId.Value, action.TradeInBondId);
            if (!bought.Ok) return BadRequest(bought.Error);

            var investmentToast = ToastBuilder.BuildInvestmentToast(
                bought.ActorName, bought.Nation, bought.BondCost, bought.TradeInCost, bought.TookControl, bought.PreviousControllerName);
            if (!SuppressBroadcasts) { await _hubContext.Clients.Group(gameId.ToString()).SendAsync("ShowToast", investmentToast, false); }
        }
        else
        {
            var passed = InvestorEngine.Pass(_context, game);
            if (!passed.Ok) return BadRequest(passed.Error);
        }

        await _context.SaveChangesAsync();
        if (!SuppressBroadcasts) { await _hubContext.Clients.All.SendAsync("GameUpdated", gameId); }

        // Trigger bot if next turn is bot-controlled
        _botService.TriggerBotTurn(gameId);

        return Ok();
    }

    public class InvestmentActionDto
    {
        public string ActionType { get; set; } = "Pass"; // "Pass" or "Buy"
        public Guid? BondId { get; set; }
        public Guid? TradeInBondId { get; set; }
    }

    [HttpPost("{gameId}/build-factory/{territoryId}")]
    public async Task<IActionResult> BuildFactory(Guid gameId, string territoryId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var game = await _context.Games
            .Include(g => g.NationStates)
            .Include(g => g.Players)
            .Include(g => g.TerritoryStates)
            .Include(g => g.Units)
            .AsSplitQuery().FirstOrDefaultAsync(g => g.Id == gameId);

        if (game == null) return NotFound();

        // Controller Check - the caller must be the acting nation's government, before the engine mutates.
        var nationState = game.NationStates.First(n => n.Nation == game.CurrentTurnNation);
        if (nationState.ControllerId == null) return BadRequest("No controller for this nation.");
        var controller = game.Players.First(p => p.Id == nationState.ControllerId);
        if (controller.UserId != userId) return Forbid();

        var result = FactoryEngine.BuildFactory(_context, game, territoryId);
        if (!result.Ok) return BadRequest(result.Error);

        await _context.SaveChangesAsync();
        if (!SuppressBroadcasts) { await _hubContext.Clients.All.SendAsync("GameUpdated", gameId); }

        return Ok();
    }

    [HttpPost("{gameId}/end-turn")]
    public async Task<IActionResult> EndTurn(Guid gameId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var game = await _context.Games
            .Include(g => g.NationStates)
            .Include(g => g.Players)
            .Include(g => g.Units)
            .AsSplitQuery().FirstOrDefaultAsync(g => g.Id == gameId);

        if (game == null) return NotFound();

        // Controller Check - the caller must be the acting nation's government, before the engine mutates.
        var nationState = game.NationStates.First(n => n.Nation == game.CurrentTurnNation);
        if (nationState.ControllerId == null) return BadRequest("No controller for this nation.");
        var controller = game.Players.First(p => p.Id == nationState.ControllerId);
        if (controller.UserId != userId) return Forbid();

        var result = TurnEngine.EndTurn(_context, game);
        if (!result.Ok) return BadRequest(result.Error);

        await _context.SaveChangesAsync();

        if (!SuppressBroadcasts) { await _hubContext.Clients.All.SendAsync("GameUpdated", gameId); }

        // Trigger bot if next nation is bot-controlled
        _botService.TriggerBotTurn(gameId);

        return Ok();
    }

    [HttpPost("{gameId}/taxation")]
    public async Task<IActionResult> ExecuteTaxation(Guid gameId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var game = await _context.Games
            .Include(g => g.NationStates)
                .ThenInclude(ns => ns.Controller)
            .Include(g => g.Players)
            .Include(g => g.Bonds)
            .Include(g => g.TerritoryStates)
            .Include(g => g.Units) // Include Units for Army/Fleet counts
            .AsSplitQuery()
            .FirstOrDefaultAsync(g => g.Id == gameId);

        if (game == null) return NotFound();

        var nation = game.CurrentTurnNation;

        // Controller Check - the caller must be the acting nation's government, before the engine mutates.
        var nationState = game.NationStates.First(n => n.Nation == nation);
        if (nationState.ControllerId == null) return BadRequest("No controller for this nation.");
        var controller = game.Players.First(p => p.Id == nationState.ControllerId);
        if (controller.UserId != userId) return Forbid();

        var result = TaxationEngine.ExecuteTaxation(_context, game);
        if (!result.Ok) return BadRequest(result.Error);

        await _context.SaveChangesAsync();

        if (result.GameEnded)
        {
            await game.SetWinnerNameAsync(_context);
            _context.Entry(game).State = EntityState.Modified;
            await _context.SaveChangesAsync();

            if (!SuppressBroadcasts) { await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId); } // Notify update FIRST so clients see 25 Power
            if (!SuppressBroadcasts) { await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameEnded", gameId); } // Notify end

            // Fire notification — but never for a replay/import: SuppressBroadcasts marks a game being
            // reconstructed by GameReplayService rather than actually played, and its players are throwaway
            // placeholder accounts. Emailing "your game finished" for a historical game someone just
            // imported is wrong (and was failing with Unauthorized against the notification function
            // anyway, since those accounts aren't real users).
            if (!SuppressBroadcasts)
            {
                _ = _notificationService.NotifyGameFinishedAsync(game, $"Ended by {nation} reaching {GameConstants.MaxPowerPoints} Power");
            }

            return Ok(new { Message = "Game Over", Winner = nation });
        }

        if (!SuppressBroadcasts) { await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId); }

        // Trigger bot if next nation is bot-controlled
        _botService.TriggerBotTurn(gameId);

        return Ok(new
        {
            TaxRevenue = result.TotalTaxRevenue,
            SoldiersPay = result.SoldiersPay,
            Bonus = result.Bonus,
            PowerGain = result.PowerGain
        });
    }

    [HttpPost("{gameId}/import")]
    public async Task<IActionResult> ExecuteImport(Guid gameId, [FromBody] ImportRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var game = await _context.Games
            .Include(g => g.NationStates)
            .Include(g => g.Players)
            .Include(g => g.Units)
            .AsSplitQuery()
            .FirstOrDefaultAsync(g => g.Id == gameId);

        if (game == null) return NotFound();

        // Controller Check - the caller must be the acting nation's government, before the engine mutates.
        var nationState = game.NationStates.First(n => n.Nation == game.CurrentTurnNation);
        if (nationState.ControllerId == null) return BadRequest("No controller.");
        var controller = game.Players.First(p => p.Id == nationState.ControllerId);
        if (controller.UserId != userId) return Forbid();

        var units = request.Units.Select(u => (u.UnitType, u.TerritoryId)).ToList();
        var result = ImportEngine.Import(_context, game, units);
        if (!result.Ok) return BadRequest(result.Error);

        await _context.SaveChangesAsync();
        if (!SuppressBroadcasts) { await _hubContext.Clients.All.SendAsync("GameUpdated", gameId); }

        return Ok();
    }

    [HttpPost("{gameId}/swissbank-response")]
    public async Task<IActionResult> SwissBankResponse(Guid gameId, [FromBody] SwissBankResponseRequest request)
    {
        var game = await _context.Games
            .Include(g => g.Players)
            .Include(g => g.NationStates)
            .Include(g => g.TerritoryStates)
            .Include(g => g.Bonds)
            .Include(g => g.Units)
            .Include(g => g.Actions)
            .AsSplitQuery()
            .FirstOrDefaultAsync(g => g.Id == gameId);

        if (game == null) return NotFound();

        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var responder = game.Players.FirstOrDefault(p => p.UserId == userId);
        if (responder == null) return Forbid();

        var result = InvestorEngine.RespondToSwissBank(_context, game, responder, request.ForceStop);
        if (!result.Ok) return BadRequest(result.Error);

        await _context.SaveChangesAsync();
        if (!SuppressBroadcasts) { await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId); }
        if (!SuppressBroadcasts) { await _hubContext.Clients.Group(gameId.ToString()).SendAsync("ShowToast", ToastBuilder.BuildSwissBankToast(result.ResponderName, result.Nation, isForceStop: result.ForcedStop), false); }
        _botService.TriggerBotTurn(gameId);
        return Ok();
    }

    [HttpPost("{gameId}/toggle-pause")]
    public async Task<IActionResult> TogglePause(Guid gameId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var game = await _context.Games
            .Include(g => g.Players).ThenInclude(p => p.User)
            .Include(g => g.NationStates)
            .AsSplitQuery()
            .FirstOrDefaultAsync(g => g.Id == gameId);

        if (game == null) return NotFound("Game not found.");
        if (game.Status != GameStatus.InProgress) return BadRequest("Game is not in progress.");

        var humanPlayers = game.Players.Where(p => !p.IsBot).ToList();
        if (humanPlayers.Count > 1) return BadRequest("Pause is only available in single-player games.");

        var myPlayer = humanPlayers.FirstOrDefault(p => p.UserId == userId);
        if (myPlayer == null) return Forbid();

        game.IsPaused = !game.IsPaused;
        _context.Entry(game).Property(g => g.IsPaused).IsModified = true;

        string actorName = myPlayer.User?.UserName ?? User.Identity?.Name ?? "Player";
        if (game.IsPaused)
        {
            GameLogger.LogPauseGame(_context, game, actorName);
        }
        else
        {
            GameLogger.LogResumeGame(_context, game, actorName);
        }

        await _context.SaveChangesAsync();

        if (!SuppressBroadcasts) { await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId); }
        if (!SuppressBroadcasts) { await _hubContext.Clients.Group(gameId.ToString()).SendAsync("ShowToast", ToastBuilder.BuildPauseToast(game.IsPaused), false); }

        if (!game.IsPaused)
        {
            _botService.TriggerBotTurn(gameId, delayMs: 0);
        }

        return Ok(new { IsPaused = game.IsPaused });
    }
}

public class SwissBankResponseRequest
{
    public bool ForceStop { get; set; }
}
