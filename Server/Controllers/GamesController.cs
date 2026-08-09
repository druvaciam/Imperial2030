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

    public GamesController(ApplicationDbContext context, UserManager<ApplicationUser> userManager, IHubContext<Imperial2030.Server.Hubs.GameHub> hubContext, Imperial2030.Server.Services.PresenceTracker presenceTracker, Imperial2030.Server.Services.BotService botService)
    {
        _context = context;
        _userManager = userManager;
        _hubContext = hubContext;
        _presenceTracker = presenceTracker;
        _botService = botService;
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<ActionResult<IEnumerable<GameDto>>> GetGames()
    {
        var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        return await _context.Games
            .Include(g => g.Players)
            .Include(g => g.NationStates)
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
                UserIds = g.Players.Select(p => p.UserId).ToList(),
                HostId = g.Players.Where(p => p.IsHost).Select(p => p.UserId).FirstOrDefault(),
                MaxPower = g.NationStates.Any() ? g.NationStates.Max(ns => ns.Power) : 0,
                WinnerName = g.WinnerName
            })
            .ToListAsync();
    }

    [HttpPost]
    public async Task<ActionResult<GameDto>> CreateGame([FromBody] CreateGameRequest req)
    {
        if (User.IsInRole("Guest")) return Forbid();
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
            VariantBonusOnlyForTaxIncreases = game.VariantBonusOnlyForTaxIncreases,
            UserIds = new List<string> { userId },
            HostId = userId
        };

        await _hubContext.Clients.All.SendAsync("GameCreated", gameDto);

        return CreatedAtAction(nameof(GetGames), new { id = game.Id }, gameDto);
    }

    [HttpPost("{gameId}/join")]
    public async Task<IActionResult> JoinGame(Guid gameId, [FromBody] JoinGameRequest? req)
    {
        if (User.IsInRole("Guest")) return Forbid();
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
        LogAction(game, "", "JoinGame");
        await _context.SaveChangesAsync();

        await _hubContext.Clients.All.SendAsync("GameUpdated", gameId);

        return Ok();
    }

    [HttpPost("{gameId}/leave")]
    public async Task<IActionResult> LeaveGame(Guid gameId)
    {
        if (User.IsInRole("Guest")) return Forbid();
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

            LogAction(game, "", "LeaveGame");
            await _context.SaveChangesAsync();
        }

        await _hubContext.Clients.All.SendAsync("GameUpdated", gameId);

        return Ok();
    }

    [HttpDelete("{gameId}")]
    public async Task<IActionResult> DeleteGame(Guid gameId)
    {
        if (User.IsInRole("Guest")) return Forbid();

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

        var player = game.Players.FirstOrDefault(p => p.UserId == userId);
        if (player == null || !player.IsHost) return Forbid();

        await _context.GameActions.Where(a => a.GameId == gameId).ExecuteDeleteAsync();

        _context.Bonds.RemoveRange(game.Bonds);
        _context.NationStates.RemoveRange(game.NationStates);
        _context.TerritoryStates.RemoveRange(game.TerritoryStates);
        _context.Units.RemoveRange(game.Units);
        _context.Players.RemoveRange(game.Players);

        _context.Games.Remove(game);
        await _context.SaveChangesAsync();

        await _hubContext.Clients.All.SendAsync("GameDeleted", gameId);
        await _hubContext.Clients.All.SendAsync("GameUpdated", gameId);
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

        var dto = new GameDetailDto
        {
            Id = game.Id,
            Name = game.Name,
            Status = game.Status,
            CreatedAt = game.CreatedAt,
            FinishedAt = game.FinishedAt,
            WinnerName = game.WinnerName,
            IsPrivate = game.IsPrivate,
            VariantBonusOnlyForTaxIncreases = game.VariantBonusOnlyForTaxIncreases,
            HostId = game.Players.FirstOrDefault(p => p.IsHost)?.UserId,
            JoinCode = game.Players.Any(p => p.UserId == userId && p.IsHost) ? game.JoinCode : null,
            CurrentTurnNation = game.CurrentTurnNation,
            PlayerCount = game.Players.Count,
            Players = game.Players.Select(p => new PlayerDto
            {
                Id = p.Id,
                UserId = p.IsBot ? $"bot-{p.Id}" : p.UserId!,
                UserName = p.IsBot ? (p.BotName ?? "Bot") : (p.User?.UserName ?? "Unknown"),
                IsHost = p.IsHost,
                Cash = p.Cash,
                IsBot = p.IsBot,
                IsOnline = p.IsBot ? true : _presenceTracker.IsUserOnline(p.UserId),
                IsActiveInGame = p.IsBot ? true : _presenceTracker.IsUserActiveInGame(gameId.ToString(), p.UserId),
                Bonds = game.Bonds.Where(b => b.HolderId == p.Id).Select(b => new BondDto
                {
                    Id = b.Id,
                    Nation = b.Nation,
                    Cost = b.Cost,
                    Interest = b.Interest,
                    HolderName = p.IsBot ? (p.BotName ?? "Bot") : (p.User?.UserName ?? "Unknown")
                }).ToList()
            }).ToList(),
            NationStates = game.NationStates.Select(ns => new NationStateDto
            {
                Nation = ns.Nation,
                Treasury = ns.Treasury,
                Power = ns.Power,
                RondelPosition = ns.RondelPosition,
                ControllerName = ns.Controller != null ? (ns.Controller.IsBot ? (ns.Controller.BotName ?? "Bot") : ns.Controller.User?.UserName) : null,
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
            Actions = game.Actions.OrderBy(a => a.Timestamp).Select(a => new GameActionDto
            {
                Id = a.Id,
                Timestamp = a.Timestamp,
                PlayerName = a.PlayerName,
                Nation = a.Nation,
                ActionType = a.ActionType,
                Message = a.Message,
                Metadata = a.Metadata ?? string.Empty
            }).ToList()
        };

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

    private static readonly string[] BotNames = { "Bot Alpha", "Bot Bravo", "Bot Charlie", "Bot Delta", "Bot Echo" };

    [HttpGet("available-bots")]
    public IActionResult GetAvailableBots()
    {
        var bots = GetAvailableBotTypes();
        return Ok(bots);
    }

    private static List<string> GetAvailableBotTypes()
    {
        var botTypes = new List<string> { "Default", "Aggressive", "Friendly", "Greedy", "Random" };

        try
        {
            var basePath = AppContext.BaseDirectory;
            if (System.IO.File.Exists(System.IO.Path.Combine(basePath, "imperial_ppo_bot.onnx")) || System.IO.File.Exists(System.IO.Path.Combine(basePath, "RL.onnx")))
            {
                botTypes.Add("RL");
            }

            var onnxFiles = System.IO.Directory.GetFiles(basePath, "*.onnx");
            foreach (var file in onnxFiles)
            {
                var fileName = System.IO.Path.GetFileNameWithoutExtension(file);
                if (!fileName.Equals("imperial_ppo_bot", StringComparison.OrdinalIgnoreCase) && !fileName.Equals("RL", StringComparison.OrdinalIgnoreCase) && fileName.StartsWith("RL", StringComparison.OrdinalIgnoreCase))
                {
                    botTypes.Add(fileName);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error discovering bot types: {ex.Message}");
        }

        return botTypes;
    }

    [HttpPost("{gameId}/add-bot")]
    public async Task<IActionResult> AddBot(Guid gameId, [FromQuery] string? botType = null)
    {
        if (User.IsInRole("Guest")) return Forbid();
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var game = await _context.Games.Include(g => g.Players).FirstOrDefaultAsync(g => g.Id == gameId);
        if (game == null) return NotFound();
        if (game.Status != GameStatus.Lobby) return BadRequest("Game must be in lobby.");

        var host = game.Players.FirstOrDefault(p => p.UserId == userId);
        if (host == null || !host.IsHost) return Forbid();
        if (game.Players.Count >= game.MaxPlayers) return BadRequest("Game is full.");

        int botIndex = game.Players.Count(p => p.IsBot);
        var botName = botIndex < BotNames.Length ? BotNames[botIndex] : $"Bot {botIndex + 1}";

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
        await _hubContext.Clients.All.SendAsync("GameUpdated", gameId);
        return Ok();
    }

    [HttpPost("{gameId}/remove-bot/{playerId}")]
    public async Task<IActionResult> RemoveBot(Guid gameId, Guid playerId)
    {
        if (User.IsInRole("Guest")) return Forbid();
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
        await _hubContext.Clients.All.SendAsync("GameUpdated", gameId);
        return Ok();
    }

    [HttpPost("{gameId}/start")]
    public async Task<IActionResult> StartGame(Guid gameId)
    {
        if (User.IsInRole("Guest")) return Forbid();
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var gameCheck = await _context.Games.Include(g => g.Players).FirstOrDefaultAsync(g => g.Id == gameId);
        if (gameCheck == null) return NotFound();
        var playerCheck = gameCheck.Players.FirstOrDefault(p => p.UserId == userId);
        if (playerCheck == null) return BadRequest("You are not in this game.");
        if (!playerCheck.IsHost) return Forbid();
        if (gameCheck.Players.Count < 2) return BadRequest("Need at least 2 players to start.");
        if (gameCheck.Status != GameStatus.Lobby) return BadRequest("Game is not in lobby state.");

        try
        {
            // --- Initialization Logic ---
            // PHASE 1: Create Entities
            var newBonds = new List<Bond>();
            var newNationStates = new List<NationState>();

            foreach (Nation nation in Enum.GetValues(typeof(Nation)))
            {
                newNationStates.Add(new NationState { Nation = nation, Treasury = 0, Power = 0, GameId = gameId });
            }

            // Init Territories
            // Each nation starts with 2 factories (one Brown/Army, one LightBlue/Fleet) per Imperial 2030 rules.
            // The remaining 2 home cities can have factories built via the Factory rondel action.
            var startingFactories = new HashSet<string>
            {
                "Moscow", "Vladivostok",       // Russia
                "Beijing", "Shanghai",         // China
                "NewDelhi", "Mumbai",          // India
                "Brasilia", "RioDeJaneiro",    // Brazil
                "Chicago", "NewOrleans",       // USA
                "Paris", "London"              // Europe
            };
            var territories = Imperial2030.Shared.Constants.TerritoryData.AllTerritories;
            var newTerritoryStates = new List<TerritoryState>();
            foreach (var t in territories)
            {
                newTerritoryStates.Add(new TerritoryState { TerritoryId = t.Id, GameId = gameId, HasFactory = startingFactories.Contains(t.Id) });
            }
            _context.TerritoryStates.AddRange(newTerritoryStates);


            var bondDefinitions = new[]
            {
                new { Cost = 2, Interest = 1 }, new { Cost = 4, Interest = 2 }, new { Cost = 6, Interest = 3 },
                new { Cost = 9, Interest = 4 }, new { Cost = 12, Interest = 5 }, new { Cost = 16, Interest = 6 },
                new { Cost = 20, Interest = 7 }, new { Cost = 25, Interest = 8 }, new { Cost = 30, Interest = 9 }
            };

            foreach (Nation nation in Enum.GetValues(typeof(Nation)))
            {
                foreach (var def in bondDefinitions)
                {
                    newBonds.Add(new Bond { Nation = nation, Cost = def.Cost, Interest = def.Interest, GameId = gameId });
                }
            }

            _context.NationStates.AddRange(newNationStates);
            _context.Bonds.AddRange(newBonds);
            await _context.SaveChangesAsync();
            _context.ChangeTracker.Clear();

            // PHASE 2: Distribution Logic (Official Imperial 2030 Rules)
            var bonds = await _context.Bonds.Where(b => b.GameId == gameId).ToListAsync();
            var nationStates = await _context.NationStates.Where(ns => ns.GameId == gameId).ToListAsync();
            var players = await _context.Players.Where(p => p.GameId == gameId).OrderBy(p => p.Id).ToListAsync();

            var random = new Random();
            var shuffledPlayers = players.OrderBy(p => random.Next()).ToList();

            // Define Packages: Nation -> (Primary 9M Nation, Secondary 2M Nation)
            // Table: 
            // Russia -> Russia 9M, China 2M
            // China -> China 9M, India 2M
            // India -> India 9M, Brazil 2M
            // Brazil -> Brazil 9M, USA 2M
            // USA -> USA 9M, Europe 2M
            // Europe -> Europe 9M, Russia 2M
            var packages = new List<(Nation Primary, Nation Secondary)>
            {
                (Nation.Russia, Nation.China),
                (Nation.China, Nation.India),
                (Nation.India, Nation.Brazil),
                (Nation.Brazil, Nation.USA),
                (Nation.USA, Nation.Europe),
                (Nation.Europe, Nation.Russia)
            };

            // Map: Which player gets which packages
            var distribution = new Dictionary<Nation, Player>();
            // Key = Primary Nation of the package, Value = Player who receives it

            if (players.Count == 2)
            {
                // 2 Players: Deal China and Russia.
                // Player A (China): Gets China + Europe + Brazil
                // Player B (Russia): Gets Russia + India + USA
                var p1 = shuffledPlayers[0];
                var p2 = shuffledPlayers[1];

                // Assign explicitly based on rules "China and Russia randomly dealt"
                // Let's assume P1 got China, P2 got Russia (randomness is in shuffledPlayers)

                // P1 Packages
                distribution[Nation.China] = p1;
                distribution[Nation.Europe] = p1;
                distribution[Nation.Brazil] = p1;

                // P2 Packages
                distribution[Nation.Russia] = p2;
                distribution[Nation.India] = p2;
                distribution[Nation.USA] = p2;
            }
            else if (players.Count == 3)
            {
                // 3 Players: Deal India, Russia, China.
                // 1 (p1): India -> Gets India + USA
                // 2 (p2): Russia -> Gets Russia + Brazil
                // 3 (p3): China -> Gets China + Europe
                var p1 = shuffledPlayers[0];
                var p2 = shuffledPlayers[1];
                var p3 = shuffledPlayers[2];

                distribution[Nation.India] = p1;
                distribution[Nation.USA] = p1;

                distribution[Nation.Russia] = p2;
                distribution[Nation.Brazil] = p2;

                distribution[Nation.China] = p3;
                distribution[Nation.Europe] = p3;
            }
            else // 4-6 Players
            {
                // Each receive 1 card.
                // Shuffle packages
                var shuffledPackages = packages.OrderBy(x => random.Next()).ToList();
                for (int i = 0; i < players.Count; i++)
                {
                    // Deal 1 package to each player
                    var pkg = shuffledPackages[i];
                    distribution[pkg.Primary] = shuffledPlayers[i];
                }
                // Remaining packages are "undealt".
            }

            // Execute Transactions for Distributed Packages
            foreach (var kvp in distribution)
            {
                var primaryNation = kvp.Key;
                var player = kvp.Value;

                // Find definition to know secondary
                var def = packages.First(p => p.Primary == primaryNation);

                // Assign 9M Bond (Primary)
                var bond9M = bonds.First(b => b.Nation == def.Primary && b.Cost == 9);
                bond9M.HolderId = player.Id;
                _context.Entry(bond9M).State = EntityState.Modified;

                // Credit Treasury for Primary
                var nsPrimary = nationStates.First(ns => ns.Nation == def.Primary);
                nsPrimary.Treasury += 9;
                _context.Entry(nsPrimary).State = EntityState.Modified;

                // Assign 2M Bond (Secondary)
                var bond2M = bonds.First(b => b.Nation == def.Secondary && b.Cost == 2);
                bond2M.HolderId = player.Id;
                _context.Entry(bond2M).State = EntityState.Modified;

                // Credit Treasury for Secondary
                var nsSecondary = nationStates.First(ns => ns.Nation == def.Secondary);
                nsSecondary.Treasury += 2;
                _context.Entry(nsSecondary).State = EntityState.Modified;
            }

            await _context.SaveChangesAsync();
            _context.ChangeTracker.Clear();

            // PHASE 3: Assign Controllers
            // Rule: Controller is who holds the Flag Card.
            // Initially, Flag Card follows the distribution.
            // If not distributed, it goes to holder of 2M bond.
            // If no 2M bond holder, stays in bank (Controller = null).

            // Re-fetch bonds to see current holders
            var bondsHeld = await _context.Bonds.Where(b => b.GameId == gameId && b.HolderId != null).ToListAsync();
            var nationStatesToUpdate = await _context.NationStates.Where(ns => ns.GameId == gameId).ToListAsync();

            foreach (var ns in nationStatesToUpdate)
            {
                Player? controller = null;

                // 1. Check if this Nation package was distributed directly
                if (distribution.ContainsKey(ns.Nation))
                {
                    controller = distribution[ns.Nation];
                }
                else
                {
                    // 2. Check who owns the 2M bond of this nation
                    var bond2M = bondsHeld.FirstOrDefault(b => b.Nation == ns.Nation && b.Cost == 2);
                    if (bond2M != null)
                    {
                        // Get the player object from our list (to avoid tracking issues, finding by Id)
                        // Actually we need the ID.
                        // bond2M.HolderId is loaded.
                        // We set controllerId.
                        ns.ControllerId = bond2M.HolderId;
                        _context.Entry(ns).State = EntityState.Modified;
                        continue; // Done
                    }
                }

                // Reset Rondel Position to null (Off-Board)
                ns.RondelPosition = null;

                if (controller != null)
                {
                    ns.ControllerId = controller.Id;
                    _context.Entry(ns).State = EntityState.Modified;
                }
            }

            await _context.SaveChangesAsync();
            await _context.SaveChangesAsync();
            _context.ChangeTracker.Clear();

            // Init Investor Card Holder (Player who holds "Austria/Europe" card? No, usually starts with player to left of... 
            // Rules: "Start with Player 1" (or standard distribution).
            // Let's assign to first player sorted by ID for simplicity.
            if (players.Any())
            {
                var sorted = players.OrderBy(p => p.Id).ToList();
                var gameToInit = await _context.Games.Include(g => g.NationStates).FirstOrDefaultAsync(g => g.Id == gameId);
                if (gameToInit != null)
                {
                    var russiaNs = gameToInit.NationStates.FirstOrDefault(ns => ns.Nation == Nation.Russia);
                    var chinaNs = gameToInit.NationStates.FirstOrDefault(ns => ns.Nation == Nation.China);

                    if (russiaNs != null && russiaNs.ControllerId.HasValue)
                    {
                        var index = sorted.FindIndex(p => p.Id == russiaNs.ControllerId.Value);
                        var nextIndex = (index + 1) % sorted.Count;
                        gameToInit.InvestorCardHolderId = sorted[nextIndex].Id;
                    }
                    else if (chinaNs != null && chinaNs.ControllerId.HasValue)
                    {
                        var index = sorted.FindIndex(p => p.Id == chinaNs.ControllerId.Value);
                        var nextIndex = (index + 1) % sorted.Count;
                        gameToInit.InvestorCardHolderId = sorted[nextIndex].Id;
                    }
                    else
                    {
                        gameToInit.InvestorCardHolderId = sorted[0].Id;
                    }
                    _context.Entry(gameToInit).State = EntityState.Modified;
                }
            }
            await _context.SaveChangesAsync();



            // PHASE 4: Update Game Status and Player Cash
            var gameToUpdate = await _context.Games.Include(g => g.NationStates).FirstOrDefaultAsync(g => g.Id == gameId);
            var playersToUpdate = await _context.Players.Where(p => p.GameId == gameId).ToListAsync();
            // Count allocated packages per player to deduct cost
            // Cost per package is 11M (9M + 2M)

            if (gameToUpdate != null)
            {
                gameToUpdate.Status = GameStatus.InProgress;

                var firstNs = gameToUpdate.NationStates.FirstOrDefault(ns => ns.Nation == gameToUpdate.CurrentTurnNation);
                if (firstNs == null || !firstNs.ControllerId.HasValue)
                {
                    gameToUpdate.AdvanceTurn();
                }

                _context.Entry(gameToUpdate).State = EntityState.Modified;
            }

            int startingCash = playersToUpdate.Count switch
            {
                2 => 35,
                3 => 24,
                _ => 13
            };

            foreach (var p in playersToUpdate)
            {
                p.Cash = startingCash;
                // Count how many packages this player received
                int pkgCount = distribution.Values.Count(v => v.Id == p.Id);
                p.Cash -= pkgCount * 11;
                _context.Entry(p).State = EntityState.Modified;
            }

            await _context.SaveChangesAsync();

            var startedGame = await _context.Games.FindAsync(gameId);
            if (startedGame != null)
            {
                LogAction(startedGame, "", "StartGame");
                await _context.SaveChangesAsync();
            }

            await _hubContext.Clients.All.SendAsync("GameUpdated", gameId);
            await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameStarted", gameId);

            // Trigger bot if first nation is bot-controlled
            _botService.TriggerBotTurn(gameId);

            return Ok();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error starting game: {ex}");
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    // Helper to find next player in rotation
    private Guid GetNextPlayerId(Game game, Guid currentId)
    {
        var sortedParams = game.Players.OrderBy(p => p.Id).ToList(); // Stable sort
        var index = sortedParams.FindIndex(p => p.Id == currentId);
        if (index == -1) return currentId; // Fallback
        var nextIndex = (index + 1) % sortedParams.Count;
        return sortedParams[nextIndex].Id;
    }

    private string GenerateJoinCode()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var random = new Random();
        return new string(Enumerable.Repeat(chars, 6)
            .Select(s => s[random.Next(s.Length)]).ToArray());
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
                // Distribute to others
                foreach (var bond in bonds.Where(b => b.HolderId != controller.Id))
                {
                    var holder = game.Players.First(p => p.Id == bond.HolderId);
                    holder.Cash += bond.Interest;
                    if (context != null) context.Entry(holder).State = EntityState.Modified;
                    var holderName = holder.GetPlayerName(context);
                    if (context != null) context.GameActions.Add(new GameAction { GameId = game.Id, Timestamp = DateTime.UtcNow, PlayerName = controllerName, Nation = nationState.Nation, ActionType = "Investor", Message = $"paid {bond.Interest}M interest to {holderName}" });
                }

                // Pay Controller
                if (nationState.Treasury >= owedToController && owedToController > 0)
                {
                    nationState.Treasury -= owedToController;
                    controller.Cash += owedToController;
                    if (context != null) context.GameActions.Add(new GameAction { GameId = game.Id, Timestamp = DateTime.UtcNow, PlayerName = controllerName, Nation = nationState.Nation, ActionType = "Investor", Message = $"paid {owedToController}M interest to {controllerName}" });
                }
                else if (nationState.Treasury > 0 && owedToController > 0)
                {
                    // Partial payment to controller
                    controller.Cash += nationState.Treasury;
                    if (context != null) context.GameActions.Add(new GameAction { GameId = game.Id, Timestamp = DateTime.UtcNow, PlayerName = controllerName, Nation = nationState.Nation, ActionType = "Investor", Message = $"paid partial {nationState.Treasury}M interest to {controllerName}" });
                    nationState.Treasury = 0;
                }
                else if (owedToController > 0)
                {
                    if (context != null) context.GameActions.Add(new GameAction { GameId = game.Id, Timestamp = DateTime.UtcNow, PlayerName = controllerName, Nation = nationState.Nation, ActionType = "Investor", Message = $"unable to pay interest to {controllerName} (treasury empty)" });
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
                    if (context != null) context.GameActions.Add(new GameAction { GameId = game.Id, Timestamp = DateTime.UtcNow, PlayerName = controllerName, Nation = nationState.Nation, ActionType = "Investor", Message = $"personally contributed {paymentFromController}M to cover interest deficit" });
                }

                // Total funds available for others
                int totalForOthers = treasuryAmount + paymentFromController;

                // Distribute to others
                if (totalForOthers >= owedToOthers)
                {
                    // Full payment possible
                    foreach (var bond in bonds.Where(b => b.HolderId != controller.Id))
                    {
                        var holder = game.Players.First(p => p.Id == bond.HolderId);
                        holder.Cash += bond.Interest;
                        if (context != null) context.Entry(holder).State = EntityState.Modified;
                        var holderName = holder.GetPlayerName(context);
                        if (context != null) context.GameActions.Add(new GameAction { GameId = game.Id, Timestamp = DateTime.UtcNow, PlayerName = controllerName, Nation = nationState.Nation, ActionType = "Investor", Message = $"paid {bond.Interest}M interest to {holderName}" });
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
                            if (context != null) context.GameActions.Add(new GameAction { GameId = game.Id, Timestamp = DateTime.UtcNow, PlayerName = controllerName, Nation = nationState.Nation, ActionType = "Investor", Message = $"paid {bond.Interest}M interest to {holderName}" });
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
                                if (context != null) context.GameActions.Add(new GameAction { GameId = game.Id, Timestamp = DateTime.UtcNow, PlayerName = controllerName, Nation = nationState.Nation, ActionType = "Investor", Message = $"paid partial {remainingFunds}M interest to {holderName} (insufficient funds for full {bond.Interest}M)" });
                                remainingFunds = 0;
                            }
                            else
                            {
                                // No funds left at all
                                var holder = game.Players.First(p => p.Id == bond.HolderId);
                                var holderName = holder.GetPlayerName(context);
                                if (context != null) context.GameActions.Add(new GameAction { GameId = game.Id, Timestamp = DateTime.UtcNow, PlayerName = controllerName, Nation = nationState.Nation, ActionType = "Investor", Message = $"unable to pay {bond.Interest}M interest to {holderName} (insufficient funds)" });
                            }
                        }
                    }

                    // Any leftover funds are returned to the treasury (should be 0 here because of the partial payment logic, 
                    // unless they somehow had EXACTLY enough to pay the first bond but not others, wait if they had exactly enough, remainingFunds is 0).
                    nationState.Treasury += remainingFunds;
                }

                if (owedToController > 0)
                {
                    if (context != null) context.GameActions.Add(new GameAction { GameId = game.Id, Timestamp = DateTime.UtcNow, PlayerName = controllerName, Nation = nationState.Nation, ActionType = "Investor", Message = $"unable to pay interest to {controllerName} (treasury empty)" });
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
                if (context != null) context.GameActions.Add(new GameAction { GameId = game.Id, Timestamp = DateTime.UtcNow, PlayerName = investorName, ActionType = "InvestorBonus", Message = "received 2M Investor bonus" });
            }
        }

        // Determine investment order: Swiss Bank players first, then Investor Card Holder
        var eligibleInvestors = new List<Guid>();

        // Swiss Bank players = players who control 0 nations
        var controlledNations = game.NationStates.Where(ns => ns.ControllerId.HasValue).Select(ns => ns.ControllerId).Distinct().ToList();
        var swissBankPlayers = game.Players
            .Where(p => !controlledNations.Contains(p.Id))
            .OrderBy(p => p.Id)
            .Select(p => p.Id)
            .ToList();

        eligibleInvestors.AddRange(swissBankPlayers);

        if (game.InvestorCardHolderId.HasValue && !eligibleInvestors.Contains(game.InvestorCardHolderId.Value))
        {
            eligibleInvestors.Add(game.InvestorCardHolderId.Value);
        }

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

        // Find max
        // Tie-breaking: Existing controller wins ties? 
        // Rules: "If the sum of proper credits of several players is equal, the player among them who bought a bond of the nation most recently gets the card."
        // We don't track purchase time perfectly, but we know the Acting Player just bought one.
        // So if Acting Player ties with current controller, Acting Player takes it? 
        // Or if Acting Player ties with someone else?

        // Simpler Heuristic for now:
        // 1. Sort by Total Investment Descending.
        // 2. If Tie, and one is the current controller, keep controller.
        // 3. If Tie, and one is Acting Player (who just bought), they take it (New Arrival).

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
            // Tie
            if (currentControllerId.HasValue && candidates.Contains(currentControllerId.Value))
            {
                // Current controller is in the tie - Retain Control
                // (Unless standard rules say new buyer takes it? 
                // Imperial 2030 Rule: "If there is a tie, the player who already held the Governance card retains it.")
                // OK, so Retain is correct.
            }
            else
            {
                // Current controller is NOT in the tie (e.g. was overtaken by two others)
                // OR there was no controller.
                // Give to Acting Player if they are in the tie (they triggered the change)
                if (game.ActingPlayerId.HasValue && candidates.Contains(game.ActingPlayerId.Value))
                {
                    nationState.ControllerId = game.ActingPlayerId.Value;
                    if (context != null) context.Entry(nationState).State = EntityState.Modified;
                }
                else
                {
                    // Fallback: Pick first
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
        if (game == null) return NotFound();
        if (game.Status != GameStatus.InProgress) return BadRequest("Game not in progress.");
        if (game.IsInvestorTurn) return BadRequest("Waiting for Investor Phase.");
        if (game.CurrentTurnNation != nation) return BadRequest($"It is {game.CurrentTurnNation}'s turn.");
        if (targetSlot < 0 || targetSlot > 7) return BadRequest($"Invalid slot {targetSlot}. Must be 0-7.");

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

            int distance = (targetSlot - currentSlot.Value + 8) % 8;

            if (distance == 0) return BadRequest("Must move at least 1 step."); // Should be covered by above equality check but safe.
            if (distance > 6) return BadRequest("Cannot move more than 6 spaces on the rondel.");
            if (distance > 3)
            {
                // Cost per additional step = 1 + Power Factor (Power / 5)
                int powerFactor = nationState.Power / 5;
                int costPerStep = 1 + powerFactor;
                cost = (distance - 3) * costPerStep;
            }
        }

        if (cost > 0 && controller.Cash < cost) return BadRequest($"Not enough cash. Cost: {cost}M");

        // --- Swiss Bank Intercept Logic ---
        bool crossingInvestor = false;
        if (currentSlot != null && targetSlot != 4)
        {
            int dist = (targetSlot - currentSlot.Value + 8) % 8;
            for (int i = 1; i < dist; i++) // Check intermediate steps
            {
                if ((currentSlot.Value + i) % 8 == 4)
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
                var swissBankPlayers = game.Players.Where(p => !game.NationStates.Any(ns => ns.ControllerId == p.Id)).ToList();
                if (swissBankPlayers.Any())
                {
                    game.PendingSwissBankForceNation = nation;
                    game.PendingSwissBankForceTargetSlot = targetSlot;
                    game.PendingSwissBankResponders = swissBankPlayers.Select(p => p.Id).ToList();

                    await _context.SaveChangesAsync();
                    await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId);
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

        GameLogger.LogRondelMove(_context, game, targetSlot, currentSlot, cost, nation, User.Identity?.Name ?? "System");
        await _context.SaveChangesAsync();

        // Check for Investor Slot (Index 4)
        bool triggeredInvestor = false;
        if (currentSlot != null)
        {
            // Moving from currentSlot to targetSlot (clockwise)
            // Path: (current + 1) ... targetSlot
            int dist = (targetSlot - currentSlot.Value + 8) % 8;
            for (int i = 1; i <= dist; i++)
            {
                int step = (currentSlot.Value + i) % 8;
                if (step == 4)
                {
                    triggeredInvestor = true;
                    break;
                }
            }
        }
        else
        {
            // First placement: if placed on 4
            if (targetSlot == 4) triggeredInvestor = true;
        }

        if (triggeredInvestor)
        {
            // Calculate if landed on
            // Note: The loop logic above is slightly flawed if we just check targetSlot==4 for "landedOn" 
            // because distinct "pass through" vs "land on" matters for 2M bonus.
            // But for now, sticking to existing logic structure.
            bool landedOn = (targetSlot == 4);
            HandleInvestorPhase(_context, game, nationState, controller, landedOn);
        }

        // Initialize Maneuver Phase
        if (targetSlot == 3 || targetSlot == 7)
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

        await _hubContext.Clients.All.SendAsync("GameUpdated", gameId);

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

        var currentNation = game.CurrentTurnNation;
        var nationState = game.NationStates.First(n => n.Nation == currentNation);

        if (nationState.ControllerId == null) return BadRequest("No controller.");
        var controller = game.Players.First(p => p.Id == nationState.ControllerId);
        if (controller.UserId != userId) return Forbid();

        // Check Rondel Position (Production slots: 2 and 6)
        if (nationState.RondelPosition != 2 && nationState.RondelPosition != 6)
        {
            return BadRequest("Not on a Production slot.");
        }

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

            GameLogger.LogProduction(_context, game, createdUnits, producedDetails, currentNation, User.Identity?.Name ?? "System");

            await _context.SaveChangesAsync();
            await _hubContext.Clients.All.SendAsync("GameUpdated", gameId);
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

            string controlChangeMessage = "";
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

            if (oldControllerId != newControllerId)
            {
                controlChangeMessage = $" and took control of {bond.Nation}";
                if (oldControllerName != null)
                {
                    controlChangeMessage += $" from {oldControllerName}";
                }
            }

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

            string baseToastMessage = tradeInCost.HasValue
                ? $"{actingPlayer.GetPlayerName(_context)} upgraded {bond.Nation} {tradeInCost.Value}M to {bond.Cost}M bond"
                : $"{actingPlayer.GetPlayerName(_context)} bought {bond.Nation} {bond.Cost}M bond";

            await _hubContext.Clients.Group(gameId.ToString()).SendAsync("ShowToast", $"{baseToastMessage}{controlChangeMessage}", false);
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
        await _hubContext.Clients.All.SendAsync("GameUpdated", gameId);

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
        // Assuming slot 1 is Factory (based on Rondel.razor)
        if (nationState.RondelPosition != 1) return BadRequest("Nation must be on 'Factory' slot.");

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

        GameLogger.LogFactoryBuild(_context, game, territoryDef.Name, nation, User.Identity?.Name ?? "System");

        await _context.SaveChangesAsync();
        await _hubContext.Clients.All.SendAsync("GameUpdated", gameId);

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

        LogAction(game, "", "EndTurn", nation);
        await _context.SaveChangesAsync();

        await _hubContext.Clients.All.SendAsync("GameUpdated", gameId);

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

        // Validate Rondel Position: Must be on Taxation (Slot 0)
        // Assuming slot 0 is Taxation based on Rondel.razor
        if (nationState.RondelPosition != 0) return BadRequest("Nation must be on 'Taxation' slot.");

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
        GameLogger.LogTaxation(_context, game, result.TotalTaxRevenue, result.SoldiersPay, treasuryGain, result.Bonus, result.PowerGain, nation, User.Identity?.Name ?? "System");
        await _context.SaveChangesAsync();

        // --- Game End Check ---
        if (nationState.Power >= 25)
        {
            game.Status = GameStatus.Finished;
            game.FinishedAt = DateTime.UtcNow;

            await game.SetWinnerNameAsync(_context);

            _context.Entry(game).State = EntityState.Modified;
            await _context.SaveChangesAsync();
            await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId); // Notify update FIRST so clients see 25 Power
            await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameEnded", gameId); // Notify end
            return Ok(new { Message = "Game Over", Winner = nation });
        }

        // --- Step 5: Turn Advance ---
        // Same logic as EndTurn (resets all turn state flags automatically)
        game.AdvanceTurn();

        _context.Entry(game).State = EntityState.Modified;
        await _context.SaveChangesAsync();

        await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId);

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

        // 5 is Import slot
        if (nationState.RondelPosition != 5) return BadRequest("Not in Import phase.");
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
        GameLogger.LogImport(_context, game, request.Units.Count, importTuples, game.CurrentTurnNation, User.Identity?.Name ?? "System");
        await _context.SaveChangesAsync();

        await _hubContext.Clients.All.SendAsync("GameUpdated", gameId);

        return Ok();
    }

    private void LogAction(Game game, string message, string type, Nation? nation = null)
    {
        GameLogger.LogAction(_context, game, message, type, nation, User.Identity?.Name ?? "System");
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
            int targetSlot = 4;
            int? currentSlot = nationState.RondelPosition;
            int cost = 0;
            if (currentSlot != null)
            {
                int distance = (targetSlot - currentSlot.Value + 8) % 8;
                if (distance > 3) cost = (distance - 3) * (1 + (nationState.Power / 5));
            }

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
            await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId);
            await _hubContext.Clients.Group(gameId.ToString()).SendAsync("ShowToast", $"{responderName} forced {nationState.Nation} to stop on Investor.", false);
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
                int cost = 0;
                if (currentSlot != null)
                {
                    int distance = (targetSlot - currentSlot.Value + 8) % 8;
                    if (distance > 3) cost = (distance - 3) * (1 + (nationState.Power / 5));
                }

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
                await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId);
                await _hubContext.Clients.Group(gameId.ToString()).SendAsync("ShowToast", $"{responderName} passed on forcing {nationState.Nation} to stop.", false);
                _botService.TriggerBotTurn(gameId);
                return Ok();
            }
            else
            {
                await _context.SaveChangesAsync();
                await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId);
                await _hubContext.Clients.Group(gameId.ToString()).SendAsync("ShowToast", $"{responderName} passed on forcing {nationState.Nation} to stop.", false);
                _botService.TriggerBotTurn(gameId);
                return Ok();
            }
        }
    }
}

public class SwissBankResponseRequest
{
    public bool ForceStop { get; set; }
}
