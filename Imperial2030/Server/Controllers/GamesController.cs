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
                PlayerCount = g.Players.Count,
                UserIds = g.Players.Select(p => p.UserId).ToList()
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
            PlayerCount = 1,
            UserIds = new List<string> { userId }
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

        var game = await _context.Games
            .Include(g => g.Players)
            .Include(g => g.Bonds)
            .Include(g => g.NationStates)
            .FirstOrDefaultAsync(g => g.Id == gameId);

        if (game == null) return NotFound();

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
        
        _context.Players.Remove(player);

        // If the player was the host, assign a new host if there are other players
        if (player.IsHost)
        {
            var newHost = game.Players.FirstOrDefault(p => p.UserId != userId);
            if (newHost != null)
            {
                newHost.IsHost = true;
                _context.Entry(newHost).State = EntityState.Modified;
            }
        }

        await _context.SaveChangesAsync();

        // If no players left, delete the game
        // We need to re-check count. Accessing game.Players might be stale if we didn't reload or if tracking didn't update list count immediately for Remove?
        // _context.Players.Remove DOES remove from the collection locally.
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

                if (controller != null)
                {
                    ns.ControllerId = controller.Id;
                    _context.Entry(ns).State = EntityState.Modified;
                }
            }
            
            await _context.SaveChangesAsync();
            _context.ChangeTracker.Clear();


            // PHASE 4: Update Game Status and Player Cash
            var gameToUpdate = await _context.Games.FirstOrDefaultAsync(g => g.Id == gameId);
            var playersToUpdate = await _context.Players.Where(p => p.GameId == gameId).ToListAsync();
            // Count allocated packages per player to deduct cost
            // Cost per package is 11M (9M + 2M)

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
                // Count how many packages this player received
                int pkgCount = distribution.Values.Count(v => v.Id == p.Id);
                p.Cash -= pkgCount * 11; 
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
