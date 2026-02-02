using Imperial2030.Server.Data;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;

namespace Imperial2030.Server.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize]
public class GamesController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IHubContext<Imperial2030.Server.Hubs.GameHub> _hubContext;

    public GamesController(ApplicationDbContext context, UserManager<ApplicationUser> userManager, IHubContext<Imperial2030.Server.Hubs.GameHub> hubContext)
    {
        _context = context;
        _userManager = userManager;
        _hubContext = hubContext;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<GameDto>>> GetGames()
    {
        return await _context.Games
            .Include(g => g.Players)
            .OrderByDescending(g => g.CreatedAt)
            .Select(g => new GameDto
            {
                Id = g.Id,
                Name = g.Name,
                Status = g.Status,
                CreatedAt = g.CreatedAt,
                PlayerCount = g.Players.Count
            })
            .ToListAsync();
    }

    [HttpPost]
    public async Task<ActionResult<GameDto>> CreateGame([FromBody] string gameName)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var game = new Game { Name = gameName };
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
            PlayerCount = 1
        };

        await _hubContext.Clients.All.SendAsync("GameCreated", gameDto);

        return CreatedAtAction(nameof(GetGames), new { id = game.Id }, gameDto);
    }

    [HttpPost("{gameId}/join")]
    public async Task<IActionResult> JoinGame(Guid gameId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var game = await _context.Games.Include(g => g.Players).FirstOrDefaultAsync(g => g.Id == gameId);
        if (game == null) return NotFound();

        if (game.Players.Any(p => p.UserId == userId))
            return BadRequest("You are already in this game.");

        if (game.Players.Count >= 6)
            return BadRequest("Game is full.");

        var player = new Player
        {
            GameId = game.Id,
            UserId = userId,
            IsHost = false
        };
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        await _hubContext.Clients.All.SendAsync("GameUpdated", gameId);

        return Ok();
    }
    [HttpPost("{gameId}/leave")]
    public async Task<IActionResult> LeaveGame(Guid gameId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var game = await _context.Games.Include(g => g.Players).FirstOrDefaultAsync(g => g.Id == gameId);
        if (game == null) return NotFound();

        var player = game.Players.FirstOrDefault(p => p.UserId == userId);
        if (player == null) return BadRequest("You are not in this game.");

        _context.Players.Remove(player);

        // If the player was the host, assign a new host if there are other players
        if (player.IsHost)
        {
            var newHost = game.Players.FirstOrDefault(p => p.UserId != userId);
            if (newHost != null)
            {
                newHost.IsHost = true;
            }
        }

        await _context.SaveChangesAsync();

        // If no players left, delete the game
        // Reload game to check players count correctly after save (or just check the tracked collection if it was updated)
        // EF Core tracks the collection, but let's be safe and check the database count or the in-memory collection which should be updated.
        // Actually, _context.Players.Remove(player) removes it from the collection on the context.
        if (!game.Players.Any())
        {
            _context.Games.Remove(game);
            await _context.SaveChangesAsync();
        }
        
        await _hubContext.Clients.All.SendAsync("GameUpdated", gameId);

        return Ok();
    }

    [HttpGet("{gameId}")]
    public async Task<ActionResult<GameDetailDto>> GetGame(Guid gameId)
    {
        var game = await _context.Games
            .Include(g => g.Players)
                .ThenInclude(p => p.User)
            .Include(g => g.NationStates) // Maps to DB
                .ThenInclude(ns => ns.Controller) // Include Controller
                    .ThenInclude(c => c.User) // Include User for Name
            .Include(g => g.Bonds)
                .ThenInclude(b => b.Holder)
                    .ThenInclude(h => h.User)
            .FirstOrDefaultAsync(g => g.Id == gameId);

        if (game == null) return NotFound();

        return new GameDetailDto
        {
            Id = game.Id,
            Name = game.Name,
            Status = game.Status,
            CreatedAt = game.CreatedAt,
            PlayerCount = game.Players.Count,
            Players = game.Players.Select(p => new PlayerDto
            {
                Id = p.Id,
                UserId = p.UserId,
                UserName = p.User?.UserName ?? "Unknown",
                IsHost = p.IsHost,
                Cash = p.Cash,
                Bonds = game.Bonds.Where(b => b.HolderId == p.Id).Select(b => new BondDto
                {
                    Id = b.Id,
                    Nation = b.Nation,
                    Cost = b.Cost,
                    Interest = b.Interest,
                    HolderName = p.User?.UserName ?? "Unknown"
                }).ToList()
            }).ToList(),
            NationStates = game.NationStates.Select(ns => new NationStateDto
            {
                Nation = ns.Nation,
                Treasury = ns.Treasury,
                Power = ns.Power,
                ControllerName = ns.Controller?.User?.UserName
            }).ToList(),
            AvailableBonds = game.Bonds.Where(b => b.HolderId == null).Select(b => new BondDto
            {
                Id = b.Id,
                Nation = b.Nation,
                Cost = b.Cost,
                Interest = b.Interest,
                HolderName = null
            }).ToList()
        };
    }

    [HttpPost("{gameId}/start")]
    public async Task<IActionResult> StartGame(Guid gameId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        // Initial check relative to user
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
            // PHASE 1: Create Entities (Bonds, NationStates)
            // We do not attach them to the 'game' object to avoid tracking issues with the existing graph.
            // We just add them to the Context directly using the GameId FK.

            var newBonds = new List<Bond>();
            var newNationStates = new List<NationState>();

            // 1. Initialize Nation States
            foreach (Nation nation in Enum.GetValues(typeof(Nation)))
            {
                newNationStates.Add(new NationState
                {
                    Nation = nation,
                    Treasury = 0,
                    Power = 0,
                    GameId = gameId
                });
            }

            // 2. Initialize Bonds (9 per nation)
            var bondDefinitions = new[]
            {
                new { Cost = 2, Interest = 1 },
                new { Cost = 4, Interest = 2 },
                new { Cost = 6, Interest = 3 },
                new { Cost = 9, Interest = 4 },
                new { Cost = 12, Interest = 5 },
                new { Cost = 16, Interest = 6 },
                new { Cost = 20, Interest = 7 },
                new { Cost = 25, Interest = 8 },
                new { Cost = 30, Interest = 9 }
            };

            foreach (Nation nation in Enum.GetValues(typeof(Nation)))
            {
                foreach (var def in bondDefinitions)
                {
                    newBonds.Add(new Bond
                    {
                        Nation = nation,
                        Cost = def.Cost,
                        Interest = def.Interest,
                        GameId = gameId
                    });
                }
            }

            _context.NationStates.AddRange(newNationStates);
            _context.Bonds.AddRange(newBonds);
            await _context.SaveChangesAsync();
            
            // PHASE 2: Assign Relationships
            // Clear tracker to ensure we work with fresh data
            _context.ChangeTracker.Clear();
            
            // Fetch what we need
            var bonds = await _context.Bonds.Where(b => b.GameId == gameId).ToListAsync();
            var nationStates = await _context.NationStates.Where(ns => ns.GameId == gameId).ToListAsync();
            var players = await _context.Players.Where(p => p.GameId == gameId).OrderBy(p => p.Id).ToListAsync(); // Order for deterministic shuffle seed if needed, but we will shuffle.

            // 4. Distribute Nations and Assets
            var random = new Random();
            var shuffledPlayers = players.OrderBy(p => random.Next()).ToList();
            var nations = Enum.GetValues(typeof(Nation)).Cast<Nation>().ToList(); 
            
            int playerIndex = 0;
            foreach (var nation in nations)
            {
                var owner = shuffledPlayers[playerIndex];
                
                var bond9M = bonds.First(b => b.Nation == nation && b.Cost == 9);
                bond9M.HolderId = owner.Id; // Set FK directly
                _context.Entry(bond9M).State = EntityState.Modified;
                
                var nationState = nationStates.First(ns => ns.Nation == nation);
                nationState.ControllerId = owner.Id; // Set FK directly
                _context.Entry(nationState).State = EntityState.Modified;
                
                playerIndex = (playerIndex + 1) % shuffledPlayers.Count;
            }
            
            await _context.SaveChangesAsync();

            // PHASE 3: Update Game Status and Player Cash
            _context.ChangeTracker.Clear();

            var gameToUpdate = await _context.Games.FirstOrDefaultAsync(g => g.Id == gameId);
            var playersToUpdate = await _context.Players.Where(p => p.GameId == gameId).ToListAsync();
            // We need to know who holds what. Re-fetch bonds with holders? 
            // Or just fetch bonds count per player.
            var bondsHeld = await _context.Bonds.Where(b => b.GameId == gameId && b.HolderId != null && b.Cost == 9).ToListAsync();

            if (gameToUpdate != null)
            {
                gameToUpdate.Status = GameStatus.InProgress;
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
                int count = bondsHeld.Count(b => b.HolderId == p.Id);
                p.Cash -= count * 9;
                _context.Entry(p).State = EntityState.Modified;
            }

            await _context.SaveChangesAsync();
            
            await _hubContext.Clients.All.SendAsync("GameUpdated", gameId);
            await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameStarted", gameId);

            return Ok();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error starting game: {ex}");
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }
}
