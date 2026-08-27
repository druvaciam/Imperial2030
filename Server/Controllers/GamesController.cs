using Imperial2030.Server.Data;
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
                // GetPlayerName checks BotName before IsBot — matters because replay/import players are kept
                // IsBot=false for the duration (see PlayerHelper.GetPlayerName's own comment) while their
                // BotName already holds the correct display name; the old inline IsBot-gated logic here
                // fell through to p.User?.UserName, which is null for those synthetic players (no real
                // backing ApplicationUser), rendering as "Unknown" in the Players panel.
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
                // GetPlayerName checks BotName before IsBot (see its own comment) — replay/import players
                // are kept IsBot=false with no real backing ApplicationUser, so the old inline IsBot-gated
                // logic here always fell through to null, making ControllerName null for every nation
                // during replay. IsMyTurn() (Client) compares this against MyPlayer?.UserName (also null
                // for the replay viewer), so null==null was silently evaluating to "my turn" for every
                // nation — showing real action controls (e.g. the maneuver phase's "End Phase" button)
                // during what's supposed to be pure, non-interactive playback.
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

    // Helper to find next player in rotation
    private Guid GetNextPlayerId(Game game, Guid currentId)
    {
        return PlayerHelper.GetNextPlayerId(game, currentId);
    }

    private string GenerateJoinCode()
    {
        return JoinCodeGenerator.Generate();
    }

    public static void HandleInvestorPhase(ApplicationDbContext? context, Game game, NationState nationState, Player controller, bool isLandedOn)
    {
        var controllerName = controller.GetPlayerName(context);

        // 1. Paying out interest (ONLY if landed on)
        if (isLandedOn)
        {
            var bonds = game.Bonds.Where(b => b.Nation == nationState.Nation && b.HolderId != null).ToList();

            int owedToController = 0;
            int owedToOthers = 0;

            foreach (var bond in bonds)
            {
                if (bond.HolderId == controller.Id)
                    owedToController += bond.Interest;
                else
                    owedToOthers += bond.Interest;
            }

            // Pay Others First
            if (nationState.Treasury >= owedToOthers)
            {
                nationState.Treasury -= owedToOthers;
                // Distribute to others, one entry per HOLDER rather than per bond. Paying per bond made a
                // player holding two bonds in this nation show up as two consecutive "paid Nm interest to
                // X" lines while everyone else got one, which read as though they were being treated
                // differently — the controller's own payment below has always been logged as a single
                // combined total. Cash is identical either way; only the reporting changes.
                foreach (var holderBonds in bonds.Where(b => b.HolderId != controller.Id).GroupBy(b => b.HolderId))
                {
                    var holder = game.Players.First(p => p.Id == holderBonds.Key);
                    int owedToHolder = holderBonds.Sum(b => b.Interest);
                    holder.Cash += owedToHolder;
                    if (context != null) context.Entry(holder).State = EntityState.Modified;
                    var holderName = holder.GetPlayerName(context);
                    GameLogger.LogInvestorInterestPaid(context, game, nationState.Nation, controllerName, owedToHolder, holderName);
                }

                // Pay Controller
                if (nationState.Treasury >= owedToController && owedToController > 0)
                {
                    nationState.Treasury -= owedToController;
                    controller.Cash += owedToController;
                    GameLogger.LogInvestorInterestPaid(context, game, nationState.Nation, controllerName, owedToController, controllerName);
                }
                else if (nationState.Treasury > 0 && owedToController > 0)
                {
                    // Partial payment to controller
                    controller.Cash += nationState.Treasury;
                    GameLogger.LogInvestorInterestPartial(context, game, nationState.Nation, controllerName, nationState.Treasury, owedToController, controllerName);
                    nationState.Treasury = 0;
                }
                else if (owedToController > 0)
                {
                    GameLogger.LogInvestorUnableToPay(context, game, nationState.Nation, controllerName, owedToController, controllerName, true, true);
                }
            }
            else
            {
                // Treasury insufficient for others
                int treasuryAmount = nationState.Treasury;
                nationState.Treasury = 0;

                // Calculate how much the controller can actually cover
                int deficit = owedToOthers - treasuryAmount;
                int paymentFromController = Math.Min(controller.Cash, deficit); // Cap at available cash

                controller.Cash -= paymentFromController;
                if (paymentFromController > 0)
                {
                    GameLogger.LogInvestorPersonallyContributed(context, game, nationState.Nation, controllerName, paymentFromController);
                }

                // Total funds available for others
                int totalForOthers = treasuryAmount + paymentFromController;

                // Distribute to others
                if (totalForOthers >= owedToOthers)
                {
                    // Full payment possible — grouped per holder for the same reason as the branch above.
                    foreach (var holderBonds in bonds.Where(b => b.HolderId != controller.Id).GroupBy(b => b.HolderId))
                    {
                        var holder = game.Players.First(p => p.Id == holderBonds.Key);
                        int owedToHolder = holderBonds.Sum(b => b.Interest);
                        holder.Cash += owedToHolder;
                        if (context != null) context.Entry(holder).State = EntityState.Modified;
                        var holderName = holder.GetPlayerName(context);
                        GameLogger.LogInvestorInterestPaid(context, game, nationState.Nation, controllerName, owedToHolder, holderName);
                    }
                }
                else
                {
                    // Partial payment (Lowest denomination first)
                    // Order bonds held by others by lowest interest (or lowest cost, they are correlated)
                    var otherBonds = bonds.Where(b => b.HolderId != controller.Id).OrderBy(b => b.Interest).ToList();
                    int remainingFunds = totalForOthers;
                    foreach (var bond in otherBonds)
                    {
                        if (remainingFunds >= bond.Interest)
                        {
                            var holder = game.Players.First(p => p.Id == bond.HolderId);
                            holder.Cash += bond.Interest;
                            if (context != null) context.Entry(holder).State = EntityState.Modified;
                            var holderName = holder.GetPlayerName(context);
                            GameLogger.LogInvestorInterestPaid(context, game, nationState.Nation, controllerName, bond.Interest, holderName);
                            remainingFunds -= bond.Interest;
                        }
                        else
                        {
                            // Not enough to pay this bond fully. Give them the remaining funds as a partial payment.
                            if (remainingFunds > 0)
                            {
                                var holder = game.Players.First(p => p.Id == bond.HolderId);
                                holder.Cash += remainingFunds;
                                if (context != null) context.Entry(holder).State = EntityState.Modified;
                                var holderName = holder.GetPlayerName(context);
                                GameLogger.LogInvestorInterestPartial(context, game, nationState.Nation, controllerName, remainingFunds, bond.Interest, holderName);
                                remainingFunds = 0;
                            }
                            else
                            {
                                // No funds left at all
                                var holder = game.Players.First(p => p.Id == bond.HolderId);
                                var holderName = holder.GetPlayerName(context);
                                GameLogger.LogInvestorUnableToPay(context, game, nationState.Nation, controllerName, bond.Interest, holderName, false, holderName == controllerName);
                            }
                        }
                    }

                    // Any leftover funds are returned to the treasury (should be 0 here because of the partial payment logic, 
                    // unless they somehow had EXACTLY enough to pay the first bond but not others, wait if they had exactly enough, remainingFunds is 0).
                    nationState.Treasury += remainingFunds;
                }

                if (owedToController > 0)
                {
                    GameLogger.LogInvestorUnableToPay(context, game, nationState.Nation, controllerName, owedToController, controllerName, true, true);
                }
            }
        }

        // 2. Activating the Investor
        // 2M Bonus
        if (game.InvestorCardHolderId.HasValue)
        {
            var investor = game.Players.FirstOrDefault(p => p.Id == game.InvestorCardHolderId.Value);
            if (investor != null)
            {
                investor.Cash += 2;
                if (context != null) context.Entry(investor).State = EntityState.Modified;
                var investorName = investor.GetPlayerName(context);
                GameLogger.LogInvestorBonus(context, game, investorName, 2);
            }
        }

        // Determine investment order. Imperial-2030-Rules.pdf p.11 numbers these steps: "2. Activating the
        // Investor" - the card holder takes the 2M above and invests - and only then "3. Investing as Swiss
        // Bank". The card holder therefore picks FIRST; bonds are a scarce shared pool and the trade-in
        // mechanic makes first pick materially valuable, so this order changes who gets what.
        var eligibleInvestors = new List<Guid>();

        if (game.InvestorCardHolderId.HasValue)
        {
            eligibleInvestors.Add(game.InvestorCardHolderId.Value);
        }

        // Swiss Bank players = players who control 0 nations (p.12), taken in the order p.11 gives them:
        // "If several players have a Swiss Bank, investing is done in the order of play (clockwise),
        // starting from the player currently with the Investor card." So walk the play order rotated to
        // begin at the card holder rather than from its arbitrary head.
        var playOrder = game.Players.GetOrderedPlayers().Select(p => p.Id).ToList();
        int rotation = game.InvestorCardHolderId.HasValue ? playOrder.IndexOf(game.InvestorCardHolderId.Value) : -1;
        if (rotation < 0) rotation = 0; // no investor card in play: nothing to count from, keep play order

        var controlledNations = game.NationStates.Where(ns => ns.ControllerId.HasValue).Select(ns => ns.ControllerId).Distinct().ToList();

        var swissBankPlayers = Enumerable.Range(0, playOrder.Count)
            .Select(i => playOrder[(rotation + i) % playOrder.Count])
            .Where(id => !controlledNations.Contains(id))
            // A card holder who also holds a Swiss Bank does not get a second turn - FAQ p.14: "Can the
            // investor invest twice if he owns a Swiss Bank? No." They are already queued above.
            .Where(id => !eligibleInvestors.Contains(id))
            .ToList();

        eligibleInvestors.AddRange(swissBankPlayers);

        if (eligibleInvestors.Any())
        {
            game.IsInvestorTurn = true;
            game.ActingPlayerId = eligibleInvestors.First();
            game.PendingInvestorIds = eligibleInvestors.Skip(1).ToList();
        }
    }

    public static void UpdateNationController(ApplicationDbContext? context, Game game, Nation nation)
    {
        var nationState = game.NationStates.First(n => n.Nation == nation);
        var bonds = game.Bonds.Where(b => b.Nation == nation && b.HolderId != null).ToList();

        if (!bonds.Any()) return; // No change if no bonds

        // Calculate total investment per player
        var investmentMap = new Dictionary<Guid, int>();
        foreach (var bond in bonds)
        {
            if (bond.HolderId.HasValue)
            {
                if (!investmentMap.ContainsKey(bond.HolderId.Value))
                    investmentMap[bond.HolderId.Value] = 0;

                investmentMap[bond.HolderId.Value] += bond.Cost;
            }
        }

        // Imperial-2030-Rules.pdf p.12: "If, due to the allocation of bonds, a new player has achieved the
        // highest credit sum (a tie is not sufficient), he takes over the government of that nation and is
        // given the nation flag card. If several players achieve the same highest credit sum, the player
        // first in seating order, counting from the player with the investor card, takes over the
        // government."
        //
        // So: an outright leader takes over, and a tie that includes the sitting government leaves it in
        // place ("a tie is not sufficient"). Note that "the player among them who bought a bond of the
        // nation most recently gets the card" - which an earlier comment here cited as a rule - does not
        // appear anywhere in the rulebook. Don't reason from it.

        if (!investmentMap.Any()) return;

        var currentControllerId = nationState.ControllerId;
        var topInvestor = investmentMap.OrderByDescending(kvp => kvp.Value).First();
        int maxInvestment = topInvestor.Value;

        var candidates = investmentMap.Where(kvp => kvp.Value == maxInvestment).Select(kvp => kvp.Key).ToList();

        if (candidates.Count == 1)
        {
            // Clear winner
            if (nationState.ControllerId != candidates[0])
            {
                nationState.ControllerId = candidates[0];
                if (context != null) context.Entry(nationState).State = EntityState.Modified;
            }
        }
        else
        {
            // Tie for the highest credit sum.
            if (currentControllerId.HasValue && candidates.Contains(currentControllerId.Value))
            {
                // The sitting government is among the tied leaders: "a tie is not sufficient" to displace
                // it, so it retains the nation flag card. Nothing to do.
            }
            else
            {
                // UNREACHABLE in real play, and left as a defensive fallback rather than built out.
                //
                // Reaching it needs the tied leaders to exclude the sitting government, and that cannot
                // happen: this method runs after each SINGLE bond purchase, so exactly one player's credit
                // sum changes per call, and the government always already holds the maximum (it is seeded
                // that way at setup - GameSetupHelper assigns it from the nation's 2M bond holder - and
                // every branch here preserves it). Let M be the old maximum, held by the government, and V
                // the buyer's new sum: V > M makes the buyer the sole candidate; V == M or V < M leaves the
                // government among the candidates and it retains. No path leaves it out.
                //
                // So the rulebook's own tie-break for this case - "the player first in seating order,
                // counting from the player with the investor card" (p.12) - has nothing to resolve here.
                // Do NOT implement it speculatively; if a future change can actually strand the government
                // off the maximum (e.g. bonds being returned mid-game), write the failing test first, then
                // replace this fallback with that rule.
                if (game.ActingPlayerId.HasValue && candidates.Contains(game.ActingPlayerId.Value))
                {
                    nationState.ControllerId = game.ActingPlayerId.Value;
                    if (context != null) context.Entry(nationState).State = EntityState.Modified;
                }
                else
                {
                    nationState.ControllerId = candidates[0];
                    if (context != null) context.Entry(nationState).State = EntityState.Modified;
                }
            }
        }
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
            .AsSplitQuery()
            .FirstOrDefaultAsync(g => g.Id == gameId);

        if (game == null) return NotFound();
        if (game.Status != GameStatus.InProgress) return BadRequest("Game not in progress.");
        if (game.IsInvestorTurn) return BadRequest("Waiting for Investor Phase.");
        if (game.CurrentTurnNation != nation) return BadRequest($"It is {game.CurrentTurnNation}'s turn.");
        if (targetSlot < 0 || targetSlot >= RondelData.SlotCount) return BadRequest($"Invalid slot {targetSlot}. Must be 0-{RondelData.SlotCount - 1}.");

        var nationState = game.NationStates.First(n => n.Nation == nation);

        // Controller Check
        if (nationState.ControllerId == null) return BadRequest("No controller for this nation.");

        var controller = game.Players.First(p => p.Id == nationState.ControllerId);
        if (controller.UserId != userId) return Forbid();

        // Check if already moved
        if (nationState.HasMovedThisTurn) return BadRequest("Already moved this turn.");

        // Calculate Distance and Cost
        int? currentSlot = nationState.RondelPosition;
        int cost = 0;

        if (currentSlot == null)
        {
            // First move: Free Placement to any slot
            cost = 0;
        }
        else
        {
            // Standard Move Logic
            if (currentSlot.Value == targetSlot) return BadRequest("Must move to a different slot.");

            int distance = (targetSlot - currentSlot.Value + RondelData.SlotCount) % RondelData.SlotCount;

            if (distance == 0) return BadRequest("Must move at least 1 step."); // Should be covered by above equality check but safe.
            if (distance > RondelData.MaxMoveDistance) return BadRequest($"Cannot move more than {RondelData.MaxMoveDistance} spaces on the rondel.");
            cost = RondelData.GetMoveCost(currentSlot, targetSlot, nationState.Power);
        }

        if (cost > 0 && controller.Cash < cost) return BadRequest($"Not enough cash. Cost: {cost}M");

        // --- Swiss Bank Intercept Logic ---
        bool crossingInvestor = false;
        if (currentSlot != null && targetSlot != RondelData.InvestorSlot)
        {
            int dist = (targetSlot - currentSlot.Value + RondelData.SlotCount) % RondelData.SlotCount;
            for (int i = 1; i < dist; i++) // Check intermediate steps
            {
                if ((currentSlot.Value + i) % RondelData.SlotCount == RondelData.InvestorSlot)
                {
                    crossingInvestor = true;
                    break;
                }
            }
        }

        if (crossingInvestor && game.PendingSwissBankForceNation == null)
        {
            int totalInterest = game.Bonds.Where(b => b.Nation == nation && b.HolderId != null).Sum(b => b.Interest);
            if (nationState.Treasury >= totalInterest)
            {
                // Find Swiss Bank players (players with no controlled government)
                var swissBankPlayers = game.Players.Where(p => !game.NationStates.Any(ns => ns.ControllerId == p.Id)).GetOrderedPlayers().ToList();
                if (swissBankPlayers.Any())
                {
                    game.PendingSwissBankForceNation = nation;
                    game.PendingSwissBankForceTargetSlot = targetSlot;
                    game.PendingSwissBankResponders = swissBankPlayers.Select(p => p.Id).ToList();

                    await _context.SaveChangesAsync();
                    if (!SuppressBroadcasts) { await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId); }
                    _botService.TriggerBotTurn(gameId);
                    return Ok();
                }
            }
        }
        // --- End Swiss Bank Intercept Logic ---

        // Clear the pending state just in case we are executing a deferred move
        if (game.PendingSwissBankForceNation == nation)
        {
            game.PendingSwissBankForceNation = null;
            game.PendingSwissBankForceTargetSlot = null;
            game.PendingSwissBankResponders.Clear();

        }

        // Execute Move
        controller.Cash -= cost;
        nationState.RondelPosition = targetSlot;
        nationState.HasMovedThisTurn = true;

        // Reset Action Flags for the new slot
        nationState.HasProducedThisTurn = false;
        nationState.HasBuiltThisTurn = false;
        nationState.HasImportedThisTurn = false;

        // Turn advancement is now manual via EndTurn endpoint

        // Reset Unit Movement for this nation
        foreach (var u in game.Units.Where(u => u.Nation == nation))
        {
            u.HasMoved = false;
            u.HasConvoyed = false;
            _context.Entry(u).State = EntityState.Modified;
        }
        _context.Entry(controller).State = EntityState.Modified;
        _context.Entry(nationState).State = EntityState.Modified;

        await _context.SaveChangesAsync();

        // controller.GetPlayerName resolves the real bot/player name — User.Identity?.Name is never
        // populated by GameReplayService's replay auth context (only NameIdentifier), so every rondel move
        // replayed through this endpoint was silently logged as GameConstants.SystemPlayerName instead.
        GameLogger.LogRondelMove(_context, game, targetSlot, currentSlot, cost, nation, controller.GetPlayerName(_context));
        await _context.SaveChangesAsync();

        // Check for Investor Slot (Index 4)
        bool triggeredInvestor = false;
        if (currentSlot != null)
        {
            // Moving from currentSlot to targetSlot (clockwise)
            // Path: (current + 1) ... targetSlot
            int dist = (targetSlot - currentSlot.Value + RondelData.SlotCount) % RondelData.SlotCount;
            for (int i = 1; i <= dist; i++)
            {
                int step = (currentSlot.Value + i) % RondelData.SlotCount;
                if (step == RondelData.InvestorSlot)
                {
                    triggeredInvestor = true;
                    break;
                }
            }
        }
        else
        {
            // First placement: if placed on Investor
            if (targetSlot == RondelData.InvestorSlot) triggeredInvestor = true;
        }

        if (triggeredInvestor)
        {
            // Calculate if landed on
            // Note: The loop logic above is slightly flawed if we just check targetSlot==Investor for "landedOn"
            // because distinct "pass through" vs "land on" matters for 2M bonus.
            // But for now, sticking to existing logic structure.
            bool landedOn = (targetSlot == RondelData.InvestorSlot);
            HandleInvestorPhase(_context, game, nationState, controller, landedOn);
        }

        // Initialize Maneuver Phase
        if (RondelData.IsManeuverSlot(targetSlot))
        {
            game.CurrentManeuverPhase = ManeuverPhase.Fleets;

            bool hasFleets = game.Units.Any(u => u.Nation == nation && u.UnitType == UnitType.Fleet && !u.HasMoved);
            if (!hasFleets)
            {
                game.CurrentManeuverPhase = ManeuverPhase.Armies;
                GameLogger.LogAutoSkipManeuverPhase(_context, game, "Fleets", nation, controller.GetPlayerName(_context));
            }
            if (game.CurrentManeuverPhase == ManeuverPhase.Armies)
            {
                bool hasArmies = game.Units.Any(u => u.Nation == nation && u.UnitType == UnitType.Army && !u.HasMoved);
                if (!hasArmies)
                {
                    game.CurrentManeuverPhase = ManeuverPhase.None;
                    GameLogger.LogAutoSkipManeuverPhase(_context, game, "Armies", nation, controller.GetPlayerName(_context));
                }
            }
        }
        else
        {
            game.CurrentManeuverPhase = ManeuverPhase.None;
        }

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
        if (game.Status != GameStatus.InProgress) return BadRequest("Game not in progress.");
        // The nation's turn is suspended while an Investor phase resolves, so no slot action may run.
        // Mirrors BuildFactory/ExecuteImport, which have always had this guard.
        if (game.IsInvestorTurn) return BadRequest("Waiting for Investor Phase.");

        var currentNation = game.CurrentTurnNation;
        var nationState = game.NationStates.First(n => n.Nation == currentNation);

        if (nationState.ControllerId == null) return BadRequest("No controller.");
        var controller = game.Players.First(p => p.Id == nationState.ControllerId);
        if (controller.UserId != userId) return Forbid();

        // Check Rondel Position (Production slots: 2 and 6)
        if (!RondelData.IsProductionSlot(nationState.RondelPosition ?? -1))
        {
            return BadRequest("Not on a Production slot.");
        }

        // Per-turn limit. Production is a single action taken on landing (Imperial-2030-Rules.pdf p.7,
        // "Production": each factory "may produce one army or one fleet"), not a repeatable one.
        // HasProducedThisTurn used to be written here but never read, unlike the identical guards on
        // HasBuiltThisTurn (BuildFactory) and HasImportedThisTurn (ExecuteImport) — so re-POSTing this
        // endpoint produced another full batch of free units on every call, letting a nation fill to its
        // GetMaxArmies/GetMaxFleets cap in a single turn.
        if (nationState.HasProducedThisTurn) return BadRequest("Already produced this turn.");

        var factoryTerritories = game.TerritoryStates
            .Where(t => t.HasFactory)
            .ToList();

        var createdUnits = 0;
        var producedDetails = new List<(UnitType UnitType, string TerritoryId)>();

        int createdArmies = 0;
        int createdFleets = 0;
        int currentArmies = game.Units.Count(u => u.Nation == currentNation && u.UnitType == UnitType.Army);
        int currentFleets = game.Units.Count(u => u.Nation == currentNation && u.UnitType == UnitType.Fleet);

        foreach (var tState in factoryTerritories)
        {
            var def = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == tState.TerritoryId);
            if (def == null) continue;

            if (def.Nation != currentNation) continue;

            var unitsInTerritory = game.Units.Where(u => u.TerritoryId == tState.TerritoryId).ToList();
            bool isBlockaded = unitsInTerritory.Any(u => u.Nation != currentNation && u.UnitType == UnitType.Army && u.IsHostile);

            if (isBlockaded) continue;

            UnitType typeToProduce = def.CityType == CityType.LightBlue ? UnitType.Fleet : UnitType.Army;

            if (typeToProduce == UnitType.Army && currentArmies + createdArmies >= NationData.GetMaxArmies(currentNation)) continue;
            if (typeToProduce == UnitType.Fleet && currentFleets + createdFleets >= NationData.GetMaxFleets(currentNation)) continue;

            var newUnit = new Unit
            {
                GameId = game.Id,
                Nation = currentNation,
                TerritoryId = tState.TerritoryId,
                UnitType = typeToProduce,
                IsHostile = false
            };

            _context.Units.Add(newUnit);
            createdUnits++;
            if (typeToProduce == UnitType.Army) createdArmies++;
            else createdFleets++;

            producedDetails.Add((typeToProduce, tState.TerritoryId));
        }

        if (createdUnits > 0)
        {
            nationState.HasProducedThisTurn = true;
            _context.Entry(nationState).State = EntityState.Modified;

            GameLogger.LogProduction(_context, game, createdUnits, producedDetails, currentNation, controller.GetPlayerName(_context));

            await _context.SaveChangesAsync();
            if (!SuppressBroadcasts) { await _hubContext.Clients.All.SendAsync("GameUpdated", gameId); }
            return Ok($"Produced {createdUnits} units.");
        }
        else
        {
            return Ok("No units produced (all factories blockaded or none exist).");
        }
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

        var actingPlayer = game.Players.FirstOrDefault(p => p.Id == game.ActingPlayerId);
        if (actingPlayer == null || actingPlayer.UserId != userId) return Forbid();

        if (action.ActionType == "Buy")
        {
            if (action.BondId == null) return BadRequest("BondId required.");
            var bond = game.Bonds.FirstOrDefault(b => b.Id == action.BondId);
            if (bond == null) return BadRequest("Bond not found.");
            if (bond.HolderId != null) return BadRequest("Bond already owned.");

            int cost = bond.Cost;
            int? tradeInCost = null;

            // Trade In Logic
            if (action.TradeInBondId.HasValue)
            {
                var tradeIn = game.Bonds.FirstOrDefault(b => b.Id == action.TradeInBondId.Value);
                if (tradeIn == null) return BadRequest("Trade-in bond not found.");
                if (tradeIn.HolderId != actingPlayer.Id) return BadRequest("You do not own the trade-in bond.");
                if (tradeIn.Nation != bond.Nation) return BadRequest("Trade-in must be for same nation.");
                if (tradeIn.Cost >= bond.Cost) return BadRequest("New bond must be higher value.");

                tradeInCost = tradeIn.Cost;
                cost = bond.Cost - tradeIn.Cost;

                // Return old bond to bank
                tradeIn.HolderId = null;
                _context.Entry(tradeIn).State = EntityState.Modified;
            }

            // Check funds
            if (actingPlayer.Cash < cost) return BadRequest("Insufficient funds.");

            actingPlayer.Cash -= cost;
            bond.HolderId = actingPlayer.Id;

            // Pay to Treasury
            var ns = game.NationStates.First(n => n.Nation == bond.Nation);
            ns.Treasury += cost;

            _context.Entry(ns).State = EntityState.Modified;
            _context.Entry(bond).State = EntityState.Modified;
            _context.Entry(actingPlayer).State = EntityState.Modified;

            // Update Controller Logic
            var oldControllerId = ns.ControllerId;
            UpdateNationController(_context, game, ns.Nation);
            var newControllerId = ns.ControllerId;

            string? newControllerName = null;
            string? oldControllerName = null;

            if (newControllerId.HasValue)
            {
                newControllerName = game.Players.FirstOrDefault(p => p.Id == newControllerId.Value)?.GetPlayerName(_context);
            }

            if (oldControllerId.HasValue)
            {
                oldControllerName = game.Players.FirstOrDefault(p => p.Id == oldControllerId.Value)?.GetPlayerName(_context);
            }

            bool tookControl = oldControllerId != newControllerId;

            bool isSwissBankKicked = oldControllerId.HasValue && !game.NationStates.Any(n => n.ControllerId == oldControllerId.Value);

            GameLogger.LogInvestmentBuy(
                _context,
                game,
                bond.Nation,
                bond.Cost,
                actingPlayer.GetPlayerName(_context),
                newControllerName,
                oldControllerName,
                isSwissBankKicked,
                tradeInCost);

            var investmentToast = ToastBuilder.BuildInvestmentToast(
                actingPlayer.GetPlayerName(_context), bond.Nation, bond.Cost, tradeInCost, tookControl, oldControllerName);

            if (!SuppressBroadcasts) { await _hubContext.Clients.Group(gameId.ToString()).SendAsync("ShowToast", investmentToast, false); }
        }
        else
        {
            GameLogger.LogInvestmentPass(_context, game, actingPlayer.GetPlayerName(_context));
        }

        // Advance queue
        if (game.PendingInvestorIds != null && game.PendingInvestorIds.Any())
        {
            game.ActingPlayerId = game.PendingInvestorIds.First();
            game.PendingInvestorIds = game.PendingInvestorIds.Skip(1).ToList();
        }
        else
        {
            // Pass Investor Card
            if (game.InvestorCardHolderId.HasValue)
            {
                game.InvestorCardHolderId = GetNextPlayerId(game, game.InvestorCardHolderId.Value);
            }

            // End Investor Turn
            game.IsInvestorTurn = false;
            game.ActingPlayerId = null;
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
        if (game.Status != GameStatus.InProgress) return BadRequest("Game not in progress.");
        if (game.IsInvestorTurn) return BadRequest("Waiting for Investor Phase.");

        var nation = game.CurrentTurnNation;
        var nationState = game.NationStates.First(n => n.Nation == nation);

        // Controller Check
        if (nationState.ControllerId == null) return BadRequest("No controller for this nation.");
        var controller = game.Players.First(p => p.Id == nationState.ControllerId);
        if (controller.UserId != userId) return Forbid();

        // 1. Validate Rondel Position
        if (nationState.RondelPosition != RondelData.FactorySlot) return BadRequest("Nation must be on 'Factory' slot.");

        // 1b. Validate Per Turn Limit
        if (nationState.HasBuiltThisTurn) return BadRequest("Already built factory this turn.");

        // 2. Validate Territory
        var territoryDef = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == territoryId);
        if (territoryDef == null) return BadRequest("Invalid territory.");

        // 3. Validate Home City
        if (!territoryDef.IsHomeCity(nation)) return BadRequest($"Can only build in {nation}'s home cities.");

        // 4. Validate State (No existing factory)
        var territoryState = game.TerritoryStates.FirstOrDefault(ts => ts.TerritoryId == territoryId);
        if (territoryState == null) return BadRequest("Territory state not initialized."); // Should not happen if StartGame worked
        if (territoryState.HasFactory) return BadRequest("Factory already exists.");

        // 4b. Validate no hostile foreign armies
        bool hasHostileForeignArmy = game.Units.Any(u => u.TerritoryId == territoryId && u.UnitType == UnitType.Army && u.Nation != nation && u.IsHostile);
        if (hasHostileForeignArmy) return BadRequest("Cannot build factory: hostile foreign armies are present in the city.");

        // 5. Validate Cost (5M from Nation Treasury - per User Request "The nation pays 5 million into the bank")
        const int FactoryCost = 5;
        if (nationState.Treasury < FactoryCost) return BadRequest($"Nation treasury insufficient. Need {FactoryCost}M.");

        // 6. Execute Build
        nationState.Treasury -= FactoryCost;
        territoryState.HasFactory = true;

        // Set flag
        nationState.HasBuiltThisTurn = true;

        // No turn advance here? usually Factory building is an action within the turn. 
        // The turn advances when moving on the Rondel. 
        // Wait, the "Factory" action happens AFTER moving.
        // So we update state, but do not change CurrentTurnNation (that happened in MoveNation).

        _context.Entry(nationState).State = EntityState.Modified;
        _context.Entry(territoryState).State = EntityState.Modified;

        GameLogger.LogFactoryBuild(_context, game, territoryDef.Name, nation, controller.GetPlayerName(_context));

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
        if (game.Status != GameStatus.InProgress) return BadRequest("Game not in progress.");
        if (game.IsInvestorTurn) return BadRequest("Waiting for Investor Phase.");
        if (game.PendingBattleDefenders.Any()) return BadRequest("Cannot end turn while a battle is pending.");
        if (game.CurrentManeuverPhase != ManeuverPhase.None) return BadRequest($"Finish your maneuver phase ({game.CurrentManeuverPhase}) first.");

        var nation = game.CurrentTurnNation;
        var nationState = game.NationStates.First(n => n.Nation == nation);

        // Controller Check
        if (nationState.ControllerId == null) return BadRequest("No controller for this nation.");
        var controller = game.Players.First(p => p.Id == nationState.ControllerId);
        if (controller.UserId != userId) return Forbid();

        // Advance Turn (Russia -> China -> India -> Brazil -> USA -> Europe)
        // Note: game.AdvanceTurn() handles all state flag resetting!
        game.AdvanceTurn();

        _context.Entry(game).State = EntityState.Modified;

        GameLogger.LogEndTurn(_context, game, nation, controller.GetPlayerName(_context));
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
        if (game.Status != GameStatus.InProgress) return BadRequest("Game not in progress.");
        if (game.IsInvestorTurn) return BadRequest("Waiting for Investor Phase.");

        var nation = game.CurrentTurnNation;
        var nationState = game.NationStates.First(n => n.Nation == nation);

        // Controller Check
        if (nationState.ControllerId == null) return BadRequest("No controller for this nation.");
        var controller = game.Players.First(p => p.Id == nationState.ControllerId);
        if (controller.UserId != userId) return Forbid();

        // Validate Rondel Position: Must be on Taxation
        if (nationState.RondelPosition != RondelData.TaxationSlot) return BadRequest("Nation must be on 'Taxation' slot.");

        int oldTreasury = nationState.Treasury;
        // --- Apply Centralized Taxation Logic ---
        var result = TaxationHelper.ApplyTaxation(game, nationState, controller);

        // Mark Controller as modified if they gained cash
        if (result.Bonus > 0)
        {
            _context.Entry(controller).State = EntityState.Modified;
        }

        // Save Changes
        _context.Entry(nationState).State = EntityState.Modified;
        int treasuryGain = nationState.Treasury - oldTreasury;
        GameLogger.LogTaxation(_context, game, result.TotalTaxRevenue, result.SoldiersPay, treasuryGain, result.Bonus, result.PowerGain, nation, controller.GetPlayerName(_context));
        await _context.SaveChangesAsync();

        // --- Game End Check ---
        if (nationState.Power >= GameConstants.MaxPowerPoints)
        {
            game.Status = GameStatus.Finished;
            game.FinishedAt = DateTime.UtcNow;

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

        // --- Step 5: Turn Advance ---
        // Same logic as EndTurn (resets all turn state flags automatically)
        game.AdvanceTurn();

        _context.Entry(game).State = EntityState.Modified;
        await _context.SaveChangesAsync();

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
        if (game.Status != GameStatus.InProgress) return BadRequest("Game not in progress.");
        if (game.IsInvestorTurn) return BadRequest("Waiting for Investor Phase.");

        var nationState = game.NationStates.First(n => n.Nation == game.CurrentTurnNation);
        if (nationState.ControllerId == null) return BadRequest("No controller.");

        var controller = game.Players.First(p => p.Id == nationState.ControllerId);
        if (controller.UserId != userId) return Forbid();

        if (nationState.RondelPosition != RondelData.ImportSlot) return BadRequest("Not in Import phase.");
        if (nationState.HasImportedThisTurn) return BadRequest("Already imported this turn.");

        if (request.Units.Count > 3) return BadRequest("Cannot import more than 3 units.");
        if (request.Units.Count == 0) return BadRequest("No units specified.");

        int cost = request.Units.Count; // 1M per unit
        if (nationState.Treasury < cost) return BadRequest($"Insufficient treasury. Cost: {cost}M");

        // Validate placement
        foreach (var unitReq in request.Units)
        {
            var territoryDef = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == unitReq.TerritoryId);
            if (territoryDef == null) return BadRequest($"Invalid territory: {unitReq.TerritoryId}");

            // Home Province Check
            if (territoryDef.Nation != game.CurrentTurnNation) return BadRequest($"Territory {territoryDef.Name} is not a home province of {game.CurrentTurnNation}.");

            // Hostile Army Check (Standing armies of other nations block import)
            bool hasHostileArmy = game.Units.Any(u => u.TerritoryId == unitReq.TerritoryId && u.Nation != game.CurrentTurnNation && u.UnitType == UnitType.Army && u.IsHostile);
            if (hasHostileArmy) return BadRequest($"Territory {territoryDef.Name} contains hostile armies.");

            // Fleet Harbor Check
            if (unitReq.UnitType == UnitType.Fleet)
            {
                if (territoryDef.CityType != CityType.LightBlue) return BadRequest($"Cannot place Fleet in {territoryDef.Name} (no harbor).");
            }
        }

        int currentArmies = game.Units.Count(u => u.Nation == game.CurrentTurnNation && u.UnitType == UnitType.Army);
        int currentFleets = game.Units.Count(u => u.Nation == game.CurrentTurnNation && u.UnitType == UnitType.Fleet);

        int requestedArmies = request.Units.Count(u => u.UnitType == UnitType.Army);
        int requestedFleets = request.Units.Count(u => u.UnitType == UnitType.Fleet);

        if (currentArmies + requestedArmies > NationData.GetMaxArmies(game.CurrentTurnNation))
            return BadRequest($"Cannot import {requestedArmies} armies. You already have {currentArmies} armies on the board, and the maximum allowed is {NationData.GetMaxArmies(game.CurrentTurnNation)}.");

        if (currentFleets + requestedFleets > NationData.GetMaxFleets(game.CurrentTurnNation))
            return BadRequest($"Cannot import {requestedFleets} fleets. You already have {currentFleets} fleets on the board, and the maximum allowed is {NationData.GetMaxFleets(game.CurrentTurnNation)}.");

        // Execute
        nationState.Treasury -= cost;
        nationState.HasImportedThisTurn = true;

        foreach (var unitReq in request.Units)
        {
            var newUnit = new Unit
            {
                GameId = gameId,
                Nation = game.CurrentTurnNation,
                TerritoryId = unitReq.TerritoryId,
                UnitType = unitReq.UnitType,
                IsHostile = false, // Default to standing (friendly)
                HasMoved = false
            };
            _context.Units.Add(newUnit);
        }

        _context.Entry(nationState).State = EntityState.Modified;

        var importTuples = request.Units.Select(u => (u.UnitType, u.TerritoryId)).ToList();
        GameLogger.LogImport(_context, game, request.Units.Count, importTuples, game.CurrentTurnNation, controller.GetPlayerName(_context));
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

        if (game.PendingSwissBankForceNation == null) return BadRequest("No pending Swiss Bank decision.");

        if (!game.PendingSwissBankResponders.Contains(responder.Id)) return BadRequest("You are not required to respond.");

        var nationState = game.NationStates.First(n => n.Nation == game.PendingSwissBankForceNation);
        var controller = game.Players.First(p => p.Id == nationState.ControllerId);

        if (request.ForceStop)
        {
            int targetSlot = RondelData.InvestorSlot;
            int? currentSlot = nationState.RondelPosition;
            int cost = RondelData.GetMoveCost(currentSlot, targetSlot, nationState.Power);

            game.PendingSwissBankForceNation = null;
            game.PendingSwissBankForceTargetSlot = null;
            game.PendingSwissBankResponders.Clear();


            controller.Cash -= cost;
            nationState.RondelPosition = targetSlot;
            game.ResetStateForNewMove(nationState, u => _context.Entry(u).State = EntityState.Modified);
            _context.Entry(controller).State = EntityState.Modified;
            _context.Entry(nationState).State = EntityState.Modified;

            string responderName = responder.IsBot ? (responder.BotName ?? "Bot") : (User.Identity?.Name ?? "Human");
            GameLogger.LogSwissBankForceStop(_context, game, nationState.Nation, responderName);

            string controllerName = controller.IsBot ? (controller.BotName ?? "Bot") : (_context.Users.Where(u => u.Id == controller.UserId).Select(u => u.UserName).FirstOrDefault() ?? "Human");
            GameLogger.LogRondelMove(_context, game, targetSlot, currentSlot, cost, nationState.Nation, controllerName);

            HandleInvestorPhase(_context, game, nationState, controller, isLandedOn: true);

            await _context.SaveChangesAsync();
            if (!SuppressBroadcasts) { await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId); }
            if (!SuppressBroadcasts) { await _hubContext.Clients.Group(gameId.ToString()).SendAsync("ShowToast", ToastBuilder.BuildSwissBankToast(responderName, nationState.Nation, isForceStop: true), false); }
            _botService.TriggerBotTurn(gameId);
            return Ok();
        }
        else
        {
            string responderName = responder.IsBot ? (responder.BotName ?? "Bot") : (User.Identity?.Name ?? "Human");
            GameLogger.LogSwissBankPass(_context, game, nationState.Nation, responderName);

            var responders = game.PendingSwissBankResponders;
            responders.Remove(responder.Id);
            game.PendingSwissBankResponders = responders.ToList();
            _context.Entry(game).Property(g => g.PendingSwissBankResponders).IsModified = true;

            if (!responders.Any())
            {
                int targetSlot = game.PendingSwissBankForceTargetSlot.Value;
                int? currentSlot = nationState.RondelPosition;
                int cost = RondelData.GetMoveCost(currentSlot, targetSlot, nationState.Power);

                game.PendingSwissBankForceNation = null;
                game.PendingSwissBankForceTargetSlot = null;

                controller.Cash -= cost;
                nationState.RondelPosition = targetSlot;
                game.ResetStateForNewMove(nationState, u => _context.Entry(u).State = EntityState.Modified);
                _context.Entry(controller).State = EntityState.Modified;
                _context.Entry(nationState).State = EntityState.Modified;

                string controllerName = controller.IsBot ? (controller.BotName ?? "Bot") : (_context.Users.Where(u => u.Id == controller.UserId).Select(u => u.UserName).FirstOrDefault() ?? "Human");
                GameLogger.LogRondelMove(_context, game, targetSlot, currentSlot, cost, nationState.Nation, controllerName);

                HandleInvestorPhase(_context, game, nationState, controller, isLandedOn: false);

                await _context.SaveChangesAsync();
                if (!SuppressBroadcasts) { await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId); }
                if (!SuppressBroadcasts) { await _hubContext.Clients.Group(gameId.ToString()).SendAsync("ShowToast", ToastBuilder.BuildSwissBankToast(responderName, nationState.Nation, isForceStop: false), false); }
                _botService.TriggerBotTurn(gameId);
                return Ok();
            }
            else
            {
                await _context.SaveChangesAsync();
                if (!SuppressBroadcasts) { await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId); }
                if (!SuppressBroadcasts) { await _hubContext.Clients.Group(gameId.ToString()).SendAsync("ShowToast", ToastBuilder.BuildSwissBankToast(responderName, nationState.Nation, isForceStop: false), false); }
                _botService.TriggerBotTurn(gameId);
                return Ok();
            }
        }
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
