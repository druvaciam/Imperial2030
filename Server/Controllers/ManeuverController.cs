using Imperial2030.Server.Data;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Imperial2030.Server.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize]
public class ManeuverController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IHubContext<Imperial2030.Server.Hubs.GameHub> _hubContext;

    public ManeuverController(ApplicationDbContext context, IHubContext<Imperial2030.Server.Hubs.GameHub> hubContext)
    {
        _context = context;
        _hubContext = hubContext;
    }

    [HttpPost("{gameId}/move-fleet")]
    public async Task<IActionResult> MoveFleet(Guid gameId, [FromBody] MoveUnitRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var game = await _context.Games
            .Include(g => g.Units)
            .Include(g => g.NationStates)
            .Include(g => g.Players)
            .FirstOrDefaultAsync(g => g.Id == gameId);

        if (game == null) return NotFound();
        if (game.Status != GameStatus.InProgress) return BadRequest("Game not in progress.");
        if (game.CurrentManeuverPhase != ManeuverPhase.Fleets) return BadRequest("Not in Fleet Maneuver phase.");

        // Validate Turn and Control
        var nation = game.CurrentTurnNation;
        var nationState = game.NationStates.First(n => n.Nation == nation);
        var controller = game.Players.FirstOrDefault(p => p.Id == nationState.ControllerId);
        
        if (controller == null || controller.UserId != userId) return Forbid();

        // Validate Unit
        var unit = game.Units.FirstOrDefault(u => u.Id == request.UnitId);
        if (unit == null) return NotFound("Unit not found.");
        if (unit.Nation != nation) return BadRequest("Not your unit.");
        if (unit.UnitType != UnitType.Fleet) return BadRequest("Not a fleet.");
        
        if (unit.HasMoved) return BadRequest("Unit already moved.");

        // Validate Move Adjacency and Type
        if (!MapConnectivity.Adjacency.TryGetValue(unit.TerritoryId, out var neighbors))
            return BadRequest("Invalid current territory.");
            
        if (!neighbors.Contains(request.DestinationId))
            return BadRequest("Destination is not adjacent.");

        var currentT = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == unit.TerritoryId);
        var destT = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == request.DestinationId);

        if (currentT == null || destT == null) return BadRequest("Invalid territory definition.");

        if (currentT.Type == TerritoryType.Land && destT.Type == TerritoryType.Land)
            return BadRequest("Fleets cannot move between inland territories.");

        // Execute Move
        unit.TerritoryId = request.DestinationId;
        unit.HasMoved = true;
        
        await _context.SaveChangesAsync();
        await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId);

        return Ok();
    }

    [HttpPost("{gameId}/move-army")]
    public async Task<IActionResult> MoveArmy(Guid gameId, [FromBody] MoveUnitRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var game = await _context.Games
            .Include(g => g.Units)
            .Include(g => g.NationStates)
            .Include(g => g.TerritoryStates)
            .Include(g => g.Players)
            .FirstOrDefaultAsync(g => g.Id == gameId);

        if (game == null) return NotFound();
        if (game.Status != GameStatus.InProgress) return BadRequest("Game not in progress.");
        if (game.CurrentManeuverPhase != ManeuverPhase.Armies) return BadRequest("Not in Army Maneuver phase.");

        // Validate Turn and Control
        var nation = game.CurrentTurnNation;
        var nationState = game.NationStates.First(n => n.Nation == nation);
        var controller = game.Players.FirstOrDefault(p => p.Id == nationState.ControllerId);

        if (controller == null || controller.UserId != userId) return Forbid();

        // Validate Unit
        var unit = game.Units.FirstOrDefault(u => u.Id == request.UnitId);
        if (unit == null) return NotFound("Unit not found.");
        if (unit.Nation != nation) return BadRequest("Not your unit.");
        if (unit.UnitType != UnitType.Army) return BadRequest("Not an army.");

        if (unit.HasMoved) return BadRequest("Unit already moved.");

        // Validate Move Adjacency and Type
        if (!MapConnectivity.Adjacency.TryGetValue(unit.TerritoryId, out var neighbors))
            return BadRequest("Invalid current territory.");

        var currentT = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == unit.TerritoryId);
        var destT = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == request.DestinationId);

        if (currentT == null || destT == null) return BadRequest("Invalid territory definition.");

        if (currentT.Type != TerritoryType.Land || destT.Type != TerritoryType.Land)
            return BadRequest("Armies can only move on land.");

        // Adjacency Check
        bool isAdjacent = neighbors.Contains(request.DestinationId);

        if (!isAdjacent)
        {
            // Check Rail Logic
            if (!CanMoveByRail(game, unit.TerritoryId, request.DestinationId, nation))
            {
                return BadRequest("Destination is not adjacent and no valid rail path exists.");
            }
        }

        // Execute Move
        unit.TerritoryId = request.DestinationId;
        unit.HasMoved = true;

        await _context.SaveChangesAsync();
        await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId);

        return Ok();
    }
    
    [HttpPost("{gameId}/next-phase")]
    public async Task<IActionResult> NextPhase(Guid gameId)
    {
        try 
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var game = await _context.Games
                 .Include(g => g.NationStates)
                 .Include(g => g.Players)
                 .Include(g => g.Units)
                 .Include(g => g.TerritoryStates)
                 .FirstOrDefaultAsync(g => g.Id == gameId);

            if (game == null) return NotFound();
            
            // Validate Control
            var nation = game.CurrentTurnNation;
            var nationState = game.NationStates.First(n => n.Nation == nation);
            var controller = game.Players.FirstOrDefault(p => p.Id == nationState.ControllerId);
            
            if (controller == null || controller.UserId != userId) return Forbid();
            
            // Resolve Battles First (Before Flags or Phase Change)
            ResolveBattles(game, _context);

            // Advance Phase
            switch (game.CurrentManeuverPhase)
            {
                case ManeuverPhase.Fleets:
                    game.CurrentManeuverPhase = ManeuverPhase.Armies;
                    break;
                case ManeuverPhase.Armies:
                    // Automatic Flag Placement Logic
                    var territoriesWithUnits = game.Units.Select(u => u.TerritoryId).Distinct().ToList();
                    
                    foreach (var tId in territoriesWithUnits)
                    {
                        var unitsInTerritory = game.Units.Where(u => u.TerritoryId == tId).ToList();
                        if (!unitsInTerritory.Any()) continue;

                        var firstNation = unitsInTerritory.First().Nation;
                        if (unitsInTerritory.All(u => u.Nation == firstNation))
                        {
                            var territoryDef = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == tId);
                            
                            if (territoryDef != null)
                            {
                            // Direct DB Check to avoid "Row not found" or Stale Entity issues
                            // We do NOT use game.TerritoryStates here because it might be out of sync.
                            var tState = await _context.TerritoryStates
                                .FirstOrDefaultAsync(ts => ts.GameId == game.Id && ts.TerritoryId == tId);

                            if (tState == null)
                            {
                                tState = new TerritoryState { TerritoryId = tId, GameId = game.Id };
                                _context.TerritoryStates.Add(tState);
                            }

                            // If I am the owner (home province), I control it naturally. 
                            // If I am NOT the owner, I place a flag.
                            // Update controller (Place Flag) ONLY IF NEUTRAL TERRITORY
                            // Flags are NOT placed on Home Provinces (Nation is not null)
                            bool isHomeProvince = territoryDef.Nation.HasValue;

                            if (!isHomeProvince && tState.Controller != firstNation)
                            {
                                tState.Controller = firstNation;
                            }
                        }
                    }
                }

                game.CurrentManeuverPhase = ManeuverPhase.None; 
                break;
            default:
                return BadRequest("Invalid phase transition.");
        }
        
        await _context.SaveChangesAsync();
        await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId);
        
        return Ok();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[ERROR] NextPhase Failed: {ex.Message}");
        Console.WriteLine(ex.StackTrace);
        return StatusCode(500, ex.Message);
    }
}

    private void ResolveBattles(Game game, ApplicationDbContext context)
    {
        var activeNation = game.CurrentTurnNation;
        Console.WriteLine($"[Battle] Resolving for {activeNation}");

        var potentialBattlegrounds = game.Units
            .Where(u => u.Nation == activeNation)
            .Select(u => u.TerritoryId)
            .Distinct()
            .ToList();

        foreach (var tId in potentialBattlegrounds)
        {
            var unitsInTerritory = game.Units.Where(u => u.TerritoryId == tId).ToList();
            
            var activeUnits = unitsInTerritory.Where(u => u.Nation == activeNation).ToList();
            var hostileUnits = unitsInTerritory.Where(u => u.Nation != activeNation).ToList();

            Console.WriteLine($"[Battle] Territory {tId}: {activeUnits.Count} Active vs {hostileUnits.Count} Hostile");

            if (!hostileUnits.Any()) continue;

            while (activeUnits.Count > 0 && hostileUnits.Count > 0)
            {
                var myUnit = activeUnits.First();
                context.Units.Remove(myUnit);
                activeUnits.Remove(myUnit);
                game.Units.Remove(myUnit);

                var enemyUnit = hostileUnits.First();
                context.Units.Remove(enemyUnit);
                hostileUnits.Remove(enemyUnit);
                game.Units.Remove(enemyUnit);
                
                Console.WriteLine($"[Battle] Resolved 1:1 in {tId}");
            }
        }
    }


    private bool CanMoveByRail(Game game, string startId, string endId, Nation nation)
    {
        var queue = new Queue<string>();
        var visited = new HashSet<string>();

        queue.Enqueue(startId);
        visited.Add(startId);

        while (queue.Count > 0)
        {
            var currentId = queue.Dequeue();
            if (currentId == endId) return true;

            if (MapConnectivity.Adjacency.TryGetValue(currentId, out var neighbors))
            {
                foreach (var neighborId in neighbors)
                {
                    // If neighbor IS the destination, we can move there (exit rail network)
                    if (neighborId == endId) return true;

                    if (visited.Contains(neighborId)) continue;
                    
                    var neighborDef = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == neighborId);
                    if (neighborDef == null || neighborDef.Type != TerritoryType.Land) continue;
                    
                    // Check Friendly Control (Home or Flag)
                    bool isHome = neighborDef.Nation == nation;
                    // TerritoryStates might be null if no flags placed yet?
                    var tState = game.TerritoryStates?.FirstOrDefault(ts => ts.TerritoryId == neighborId);
                    
                    // Controlled if (Home AND Not Occupied by enemy?) - simplified:
                    // If Home, controlled unless flagged by enemy. 
                    // If Neutral, controlled if flagged by me.
                    
                    // Simplified logic from before:
                    bool isControlled = (isHome && (tState == null || tState.Controller == nation)) ||
                                        (tState != null && tState.Controller == nation);
                    
                    // If not home and not controlled by us, it's not part of our rail network (cannot pass THROUGH)
                    if (!isHome && !isControlled) continue;

                    // Check Hostile Units (Any foreign unit blocks rail traversal)
                    bool hasHostileUnits = game.Units.Any(u => u.TerritoryId == neighborId && u.Nation != nation);
                    if (hasHostileUnits) continue;

                    visited.Add(neighborId);
                    queue.Enqueue(neighborId);
                }
            }
        }
        return false;
    }
}

public class MoveUnitRequest
{
    public Guid UnitId { get; set; }
    public string DestinationId { get; set; }
}
