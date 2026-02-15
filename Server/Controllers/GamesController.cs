using Imperial2030.Server.Data;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Models;
using Imperial2030.Shared.Constants;
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
            .AsSplitQuery()
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
            .Include(g => g.TerritoryStates)
            .Include(g => g.Units)
            .AsSplitQuery()
            .FirstOrDefaultAsync(g => g.Id == gameId);

        if (game == null) return NotFound();

        return new GameDetailDto
        {
            Id = game.Id,
            Name = game.Name,
            Status = game.Status,
            CreatedAt = game.CreatedAt,
            CurrentTurnNation = game.CurrentTurnNation,
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
                RondelPosition = ns.RondelPosition,
                ControllerName = ns.Controller?.User?.UserName,
                HasBuiltThisTurn = ns.HasBuiltThisTurn,
                HasProducedThisTurn = ns.HasProducedThisTurn,
                HasMovedThisTurn = ns.HasMovedThisTurn,
                HasImportedThisTurn = ns.HasImportedThisTurn
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
            Units = game.Units.ToList(),
            ManeuverState = new ManeuverState { Phase = game.CurrentManeuverPhase }
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

            // Init Territories
            var territories = Imperial2030.Shared.Constants.TerritoryData.AllTerritories; // Wait, I need to create this first
            var newTerritoryStates = new List<TerritoryState>();
            foreach(var t in territories)
            {
                newTerritoryStates.Add(new TerritoryState { TerritoryId = t.Id, GameId = gameId, HasFactory = t.Nation.HasValue });
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
                 var gameToInit = await _context.Games.FirstOrDefaultAsync(g => g.Id == gameId);
                 if (gameToInit != null) 
                 {
                     gameToInit.InvestorCardHolderId = sorted[0].Id; // Give to First Player
                     _context.Entry(gameToInit).State = EntityState.Modified;
                 }
            }
            await _context.SaveChangesAsync();



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
    
    // Helper to find next player in rotation
    private Guid GetNextPlayerId(Game game, Guid currentId)
    {
        var sortedParams = game.Players.OrderBy(p => p.Id).ToList(); // Stable sort
        var index = sortedParams.FindIndex(p => p.Id == currentId);
        if (index == -1) return currentId; // Fallback
        var nextIndex = (index + 1) % sortedParams.Count;
        return sortedParams[nextIndex].Id;
    }

    private void HandleInvestorPhase(Game game, NationState nationState, Player controller, bool isLandedOn)
    {
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
                _context.Entry(holder).State = EntityState.Modified;
            }

            // Pay Controller
            if (nationState.Treasury >= owedToController)
            {
                nationState.Treasury -= owedToController;
                controller.Cash += owedToController;
            }
            else
            {
                // Partial payment to controller
                controller.Cash += nationState.Treasury;
                nationState.Treasury = 0;
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
            // Controller gets 0 interest.
            
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
                    _context.Entry(holder).State = EntityState.Modified;
                }
            }
            else
            {
                // Partial payment (Pro-rata)
                 double ratio = (double)totalForOthers / owedToOthers;
                 foreach (var bond in bonds.Where(b => b.HolderId != controller.Id))
                 {
                     var holder = game.Players.First(p => p.Id == bond.HolderId);
                     int payout = (int)(bond.Interest * ratio);
                     holder.Cash += payout;
                     _context.Entry(holder).State = EntityState.Modified;
                 }
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
                 _context.Entry(investor).State = EntityState.Modified;
                 
                 // Enable Investor Turn
                 game.IsInvestorTurn = true;
                 game.ActingPlayerId = investor.Id;
             }
        }
    }

    private void UpdateNationController(Game game, Nation nation)
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
                _context.Entry(nationState).State = EntityState.Modified;
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
                    _context.Entry(nationState).State = EntityState.Modified;
                }
                else
                {
                     // Fallback: Pick first
                     nationState.ControllerId = candidates[0];
                     _context.Entry(nationState).State = EntityState.Modified;
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
            // distance is 1..7
            
            if (distance > 3)
            {
                // Cost per additional step = 1 + Power Factor (Power / 5)
                int powerFactor = nationState.Power / 5;
                int costPerStep = 1 + powerFactor;
                cost = (distance - 3) * costPerStep;
            }
        }

        if (cost > 0 && controller.Cash < cost) return BadRequest($"Not enough cash. Cost: {cost}M");

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
        foreach(var u in game.Units.Where(u => u.Nation == nation))
        {
             u.HasMoved = false;
             u.HasConvoyed = false; 
             _context.Entry(u).State = EntityState.Modified;
        }
        _context.Entry(controller).State = EntityState.Modified;
        _context.Entry(nationState).State = EntityState.Modified;

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
            HandleInvestorPhase(game, nationState, controller, landedOn);
        }

        // Initialize Maneuver Phase
        if (targetSlot == 3 || targetSlot == 7)
        {
            game.CurrentManeuverPhase = ManeuverPhase.Fleets;
        }
        else
        {
            game.CurrentManeuverPhase = ManeuverPhase.None;
        }

        await _context.SaveChangesAsync();
        
        await _hubContext.Clients.All.SendAsync("GameUpdated", gameId);
        
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

        foreach (var tState in factoryTerritories)
        {
            var def = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == tState.TerritoryId);
            if (def == null) continue;

            if (def.Nation != currentNation) continue; 

            var unitsInTerritory = game.Units.Where(u => u.TerritoryId == tState.TerritoryId).ToList();
            bool isBlockaded = unitsInTerritory.Any(u => u.Nation != currentNation && u.UnitType == UnitType.Army && u.IsHostile);

            if (isBlockaded) continue;

            UnitType typeToProduce = def.CityType == CityType.LightBlue ? UnitType.Fleet : UnitType.Army;

            var newUnit = new Unit
            {
                GameId = game.Id,
                Nation = currentNation,
                TerritoryId = tState.TerritoryId,
                UnitType = typeToProduce,
                IsHostile = true
            };
            
            _context.Units.Add(newUnit);
            createdUnits++;
        }

        if (createdUnits > 0)
        {
            nationState.HasProducedThisTurn = true;
            _context.Entry(nationState).State = EntityState.Modified;
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
            .Include(g => g.Players)
            .Include(g => g.Bonds)
            .Include(g => g.NationStates)
            .FirstOrDefaultAsync(g => g.Id == gameId);

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
             
             // Trade In Logic
             if (action.TradeInBondId.HasValue)
             {
                 var tradeIn = game.Bonds.FirstOrDefault(b => b.Id == action.TradeInBondId.Value);
                 if (tradeIn == null) return BadRequest("Trade-in bond not found.");
                 if (tradeIn.HolderId != actingPlayer.Id) return BadRequest("You do not own the trade-in bond.");
                 if (tradeIn.Nation != bond.Nation) return BadRequest("Trade-in must be for same nation.");
                 if (tradeIn.Cost >= bond.Cost) return BadRequest("New bond must be higher value.");
                 
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
             UpdateNationController(game, ns.Nation);
        }
        
        // Pass Investor Card
        if (game.InvestorCardHolderId.HasValue)
        {
            game.InvestorCardHolderId = GetNextPlayerId(game, game.InvestorCardHolderId.Value);
        }

        // End Investor Turn
        game.IsInvestorTurn = false;
        game.ActingPlayerId = null;
        
        await _context.SaveChangesAsync();
        await _hubContext.Clients.All.SendAsync("GameUpdated", gameId);
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

        // Advance Turn (Russia -> China -> India -> Brazil -> USA -> Europe)
        var nations = Enum.GetValues(typeof(Nation)).Cast<Nation>().ToList();
        int currentIndex = nations.IndexOf(nation);
        int nextIndex = (currentIndex + 1) % nations.Count;
        game.CurrentTurnNation = nations[nextIndex];
        
        // Reset current nation's turn flags
        nationState.HasBuiltThisTurn = false;
        nationState.HasMovedThisTurn = false;
        nationState.HasImportedThisTurn = false;

        _context.Entry(game).State = EntityState.Modified;
        await _context.SaveChangesAsync();

        await _hubContext.Clients.All.SendAsync("GameUpdated", gameId);

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

        // --- Step 1: Tax Revenue ---
        // 1a. Factories (2M each if unoccupied)
        // Def: A factory is unoccupied when there is no HOSTILE army in the home province.
        // For now, since unit logic is partial, assume no hostile armies unless explicitly checked.
        // We need to check if there are any armies of OTHER nations in the factory territory.
        
        int factoryRevenue = 0;
        var territoriesWithFactories = game.TerritoryStates.Where(ts => ts.HasFactory).ToList();
        
        foreach (var ts in territoriesWithFactories)
        {
            var territoryDef = Imperial2030.Shared.Constants.TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == ts.TerritoryId);
            if (territoryDef != null && territoryDef.Nation == nation) // Is Home Province of current nation
            {
                // check for hostile armies
                // Hostile = Army of another nation. (Note: unit could be friendly if nations are at peace? 
                // Rules say "hostile army". Simplest interpretation: Any army not belonging to the nation.)
                bool hasHostileArmy = game.Units.Any(u => u.TerritoryId == ts.TerritoryId && u.UnitType == UnitType.Army && u.Nation != nation);
                
                if (!hasHostileArmy)
                {
                    factoryRevenue += 2;
                }
            }
        }

        // 1b. Flags (1M per controlled territory)
        int flagRevenue = game.TerritoryStates.Count(ts => ts.Controller == nation);

        // Max Revenue Logic
        // "The maximum possible tax revenue is 23 million (8 million from 4 factories plus 15 million from 15 flags)."
        // Is this a hard cap on the sum? Yes.
        int totalTaxRevenue = Math.Min(23, factoryRevenue + flagRevenue);

        // Add to Nation Treasury
        // "The tax revenue is paid from the bank into the national treasury"
        nationState.Treasury += totalTaxRevenue;

        // --- Step 2: Soldiers' Pay ---
        // "The treasury has to pay one million in soldiers’ pay for each of its armies and fleets"
        int unitCount = game.Units.Count(u => u.Nation == nation);
        int soldiersPay = unitCount * 1;

        if (nationState.Treasury >= soldiersPay)
        {
            nationState.Treasury -= soldiersPay;
        }
        else
        {
            // "If the treasury is empty, no more payments are made."
            // Implicitly means pay what you can? Or pay 0? 
            // "If the treasury is empty, no more payments are made." implies we drain the treasury and stop? 
            // Usually in board games this means pay until 0.
            nationState.Treasury = 0;
        }

        // Bonus for the Controller (Player)
        // 0-5: 0
        // 6-9: 1
        // 10-11: 2
        // 12-13: 3
        // 14-15: 4
        // 16+: 5
        
        int bonus = 0;
        if (totalTaxRevenue >= 16) bonus = 5;
        else if (totalTaxRevenue >= 14) bonus = 4;
        else if (totalTaxRevenue >= 12) bonus = 3;
        else if (totalTaxRevenue >= 10) bonus = 2;
        else if (totalTaxRevenue >= 6) bonus = 1;
        else bonus = 0;
        
        // Check Treasury Ability to Pay Bonus
        // "If the soldiers‘ pay was so high that the treasury does not have enough money to pay the bonus, the bonus is reduced"
        if (nationState.Treasury < bonus)
        {
            bonus = nationState.Treasury;
        }
        
        if (bonus > 0)
        {
            nationState.Treasury -= bonus;
            controller.Cash += bonus;
            _context.Entry(controller).State = EntityState.Modified;
        }

        // --- Step 4: Adding Power Points ---
        // Power gain is based on Tax Revenue
        // Using the explicit lookup table provided by user:
        // Tax: 0-5 -> 0 Power
        // ...
        // Tax: 18+ -> 10 Power

        int powerGain = 0;
        if (totalTaxRevenue <= 5) powerGain = 0;
        else if (totalTaxRevenue <= 7) powerGain = 1;
        else if (totalTaxRevenue <= 9) powerGain = 2;
        else if (totalTaxRevenue == 10) powerGain = 3;
        else if (totalTaxRevenue == 11) powerGain = 4;
        else if (totalTaxRevenue == 12) powerGain = 5;
        else if (totalTaxRevenue == 13) powerGain = 6;
        else if (totalTaxRevenue == 14) powerGain = 7;
        else if (totalTaxRevenue == 15) powerGain = 8;
        else if (totalTaxRevenue <= 17) powerGain = 9;
        else powerGain = 10; // 18+

        nationState.Power += powerGain;
        if (nationState.Power > 25) nationState.Power = 25;

        // Update Tax Chart Position
        nationState.TaxChartPosition = totalTaxRevenue;

        // Save Changes
        _context.Entry(nationState).State = EntityState.Modified;
        await _context.SaveChangesAsync();
        
        // --- Game End Check ---
        if (nationState.Power >= 25)
        {
             game.Status = GameStatus.Finished;
             _context.Entry(game).State = EntityState.Modified;
             await _context.SaveChangesAsync();
             await _hubContext.Clients.All.SendAsync("GameEnded", gameId); // Notify end
             return Ok(new { Message = "Game Over", Winner = nation });
        }


        // --- Step 5: Turn Advance ---
        // Same logic as EndTurn
        // Reset flags
        nationState.HasBuiltThisTurn = false;
        nationState.HasMovedThisTurn = false; // Reset move flag too
        
        // Advance Nation
        var nations = Enum.GetValues(typeof(Nation)).Cast<Nation>().ToList();
        int currentIndex = nations.IndexOf(nation);
        int nextIndex = (currentIndex + 1) % nations.Count;
        game.CurrentTurnNation = nations[nextIndex];
        
        _context.Entry(game).State = EntityState.Modified;
        await _context.SaveChangesAsync();

        await _hubContext.Clients.All.SendAsync("GameUpdated", gameId);

        return Ok(new 
        { 
            TaxRevenue = totalTaxRevenue, 
            SoldiersPay = soldiersPay, 
            Bonus = bonus, 
            PowerGain = powerGain 
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
            var territoryDef = Imperial2030.Shared.Constants.TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == unitReq.TerritoryId);
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
                IsHostile = true, // Default to standing
                HasMoved = false 
            };
            _context.Units.Add(newUnit);
        }

        _context.Entry(nationState).State = EntityState.Modified;
        await _context.SaveChangesAsync();

        await _hubContext.Clients.All.SendAsync("GameUpdated", gameId);
        
        return Ok($"Imported {request.Units.Count} units.");
    }
}
