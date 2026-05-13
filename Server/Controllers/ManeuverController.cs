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
    private readonly Imperial2030.Server.Services.BotService _botService;

    public ManeuverController(ApplicationDbContext context, IHubContext<Imperial2030.Server.Hubs.GameHub> hubContext, Imperial2030.Server.Services.BotService botService)
    {
        _context = context;
        _hubContext = hubContext;
        _botService = botService;
    }

    [HttpPost("{gameId}/move-fleet")]
    public async Task<IActionResult> MoveFleet(Guid gameId, [FromBody] MoveUnitRequest request)
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

        // Canal Logic: Check if moving through Panama or Suez
        var canal = MapConnectivity.CanalLinks.FirstOrDefault(c => 
            (c.Region1 == unit.TerritoryId && c.Region2 == request.DestinationId) ||
            (c.Region1 == request.DestinationId && c.Region2 == unit.TerritoryId));

        if (canal != default)
        {
            var controllerId = canal.ControllerId;
            var tState = game.TerritoryStates.FirstOrDefault(ts => ts.TerritoryId == controllerId);
            if (tState != null && tState.Controller != null && tState.Controller != nation)
            {
                // Check if the same player controls both nations
                var canalNation = tState.Controller.Value;
                var canalNationState = game.NationStates.FirstOrDefault(ns => ns.Nation == canalNation);
                var isSamePlayer = canalNationState != null && canalNationState.ControllerId == controller.Id;

                if (!isSamePlayer)
                {
                    return BadRequest($"Passage through {controllerId} blocked by {tState.Controller}.");
                }
            }
        }

        var currentT = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == unit.TerritoryId);
        var destT = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == request.DestinationId);

        if (currentT == null || destT == null) return BadRequest("Invalid territory definition.");

        if (currentT.Type == TerritoryType.Land && destT.Type == TerritoryType.Land)
            return BadRequest("Fleets cannot move between inland territories.");

        // Execute Move
        var sourceTerritory = unit.TerritoryId;
        unit.TerritoryId = request.DestinationId;
        unit.HasMoved = true;

        // Auto-Battle Logic (If specified)
        if (request.BattleTargetNation.HasValue)
        {
            var targetNation = request.BattleTargetNation.Value;
            // Find a fleet of target nation in destination
            var enemyFleet = game.Units.FirstOrDefault(u => 
                u.TerritoryId == request.DestinationId && 
                u.Nation == targetNation);

            if (enemyFleet != null)
            {
                // Destroy Both
                _context.Units.Remove(unit);
                _context.Units.Remove(enemyFleet);
                game.Units.Remove(unit);
                game.Units.Remove(enemyFleet);
                LogAction(game, $"fleet attacked {targetNation} in {request.DestinationId}. Both destroyed", "Battle", nation);
            }
        }
        else
        {
            // Peace Move - Check for foreign fleets in the destination
            var foreignFleets = game.Units
                .Where(u => u.TerritoryId == request.DestinationId && u.UnitType == UnitType.Fleet && u.Nation != nation)
                .Select(u => u.Nation)
                .Distinct()
                .ToList();

            if (foreignFleets.Any())
            {
                // Enter Pending Battle Negotiation Phase
                game.PendingBattleTerritoryId = request.DestinationId;
                game.PendingBattleAggressorNation = nation;
                game.PendingBattleDefenders = foreignFleets.ToList();

                LogAction(game, $"fleet moved peacefully to {request.DestinationId}, awaiting response from {string.Join(", ", foreignFleets)}", "MoveFleet", nation);
            }
        }
        
        if (!game.PendingBattleDefenders.Any())
        {
            // Only log standard move and TryAutoAdvance if there's no pending battle blocking the phase.
            // If Pending, advancement and standard logging is delayed.
            LogAction(game, $"fleet moved to {request.DestinationId} from {sourceTerritory}", "MoveFleet", nation);
            await UpdateTerritoryControl(game);
            await TryAutoAdvanceManeuver(game, nation);
        }

        await _context.SaveChangesAsync();
        await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId);

        if (game.PendingBattleDefenders.Any())
        {
            _ = Task.Run(async () => { await Task.Delay(1500); await _botService.TryPlayBotTurnAsync(gameId); });
        }

        return Ok();
    }

    [HttpPost("{gameId}/battle")]
    public async Task<IActionResult> Battle(Guid gameId, [FromBody] MoveUnitRequest request)
    {
        // Stationary Battle (Fleets that do not move)
        // Request uses UnitId (Self) and BattleTargetNation (Enemy)
        // DestinationId is ignored or used as confirmation of location? Location inferred from Unit.

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var game = await _context.Games
            .Include(g => g.Units)
            .Include(g => g.NationStates)
            .Include(g => g.Players)
            .FirstOrDefaultAsync(g => g.Id == gameId);

        if (game == null) return NotFound();
        
        // Validate Turn/Control
        var nation = game.CurrentTurnNation;
        var nationState = game.NationStates.First(n => n.Nation == nation);
        var controller = game.Players.FirstOrDefault(p => p.Id == nationState.ControllerId);
        
        if (controller == null || controller.UserId != userId) return Forbid();

        var unit = game.Units.FirstOrDefault(u => u.Id == request.UnitId);
        if (unit == null) return NotFound("Unit not found.");
        if (unit.Nation != nation) return BadRequest("Not your unit.");

        
        if (unit.HasMoved) return BadRequest("Unit already moved this turn."); 
        
        if (!request.BattleTargetNation.HasValue) return BadRequest("Target nation required.");
        var targetNation = request.BattleTargetNation.Value;

        // Find enemy in same territory
        var enemyUnit = game.Units.FirstOrDefault(u => 
            u.TerritoryId == unit.TerritoryId && 
            u.Nation == targetNation); // Armies fight Armies, Fleets fight Fleets

        if (enemyUnit == null) return BadRequest($"No {targetNation} {unit.UnitType} in {unit.TerritoryId}.");

        // Destroy Both
        _context.Units.Remove(unit);
        _context.Units.Remove(enemyUnit);
        game.Units.Remove(unit);
        game.Units.Remove(enemyUnit);
        
        LogAction(game, $"{unit.UnitType.ToString().ToLower()} attacked {targetNation} in {unit.TerritoryId}. Both destroyed", "Battle", nation);
        
        await UpdateTerritoryControl(game);
        await TryAutoAdvanceManeuver(game, nation);
        
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
            if (CanMoveByRail(game, unit.TerritoryId, request.DestinationId, nation))
            {
                // Move valid by Rail - No extra cost/side effects for now
            }
            else
            {
                // Check Convoy Logic
                List<Unit>? usedFleets = null;

                if (request.ConvoyFleetIds != null && request.ConvoyFleetIds.Any())
                {
                    // Validate specific fleets provided by client
                    usedFleets = ValidateSpecificConvoyFleets(game, unit.TerritoryId, request.DestinationId, nation, request.ConvoyFleetIds);
                    if (usedFleets == null)
                    {
                        return BadRequest("Invalid convoy path with specified fleets.");
                    }
                }
                else
                {
                    // Auto-select fleets
                    usedFleets = GetConvoyFleets(game, unit.TerritoryId, request.DestinationId, nation);
                }

                if (usedFleets != null)
                {
                    // Mark fleets as used
                    foreach (var fleet in usedFleets)
                    {
                        fleet.HasConvoyed = true;
                        _context.Entry(fleet).State = EntityState.Modified;
                    }
                }
                else
                {
                    return BadRequest("Destination is not adjacent, and no valid rail or convoy path exists.");
                }
            }
        }

        // Execute Move
        var sourceTerritory = unit.TerritoryId;
        unit.TerritoryId = request.DestinationId;
        unit.HasMoved = true;

        // Auto-Battle Logic (MoveArmy)
        if (request.BattleTargetNation.HasValue)
        {
            var targetNation = request.BattleTargetNation.Value;
            var enemyUnit = game.Units.FirstOrDefault(u => 
                u.TerritoryId == request.DestinationId && 
                u.Nation == targetNation); 

            if (enemyUnit != null)
            {
                // Destroy Both
                _context.Units.Remove(unit);
                _context.Units.Remove(enemyUnit);
                game.Units.Remove(unit);
                game.Units.Remove(enemyUnit);
                LogAction(game, $"army attacked {targetNation} in {request.DestinationId}. Both destroyed", "Battle", nation);
            }
        }
        else
        {
            // Peace Move - Check for foreign armies in the destination
            var foreignArmies = game.Units
                .Where(u => u.TerritoryId == request.DestinationId && u.UnitType == UnitType.Army && u.Nation != nation)
                .Select(u => u.Nation)
                .Distinct()
                .ToList();

            if (foreignArmies.Any())
            {
                // Enter Pending Battle Negotiation Phase
                game.PendingBattleTerritoryId = request.DestinationId;
                game.PendingBattleAggressorNation = nation;
                game.PendingBattleDefenders = foreignArmies.ToList();

                LogAction(game, $"army moved peacefully to {request.DestinationId}, awaiting response from {string.Join(", ", foreignArmies)}", "MoveArmy", nation);
            }
        }
        
        if (!game.PendingBattleDefenders.Any())
        {
            LogAction(game, $"army moved to {request.DestinationId} from {sourceTerritory}", "MoveArmy", nation);
            await UpdateTerritoryControl(game);
            await TryAutoAdvanceManeuver(game, nation);
        }

        await _context.SaveChangesAsync();
        Console.WriteLine("[MoveArmy] Changes Saved.");
        await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId);

        if (game.PendingBattleDefenders.Any())
        {
            _ = Task.Run(async () => { await Task.Delay(1500); await _botService.TryPlayBotTurnAsync(gameId); });
        }

        return Ok();
    }
    
    [HttpPost("{gameId}/destroy-factory")]
    public async Task<IActionResult> DestroyFactory(Guid gameId, [FromBody] DestroyFactoryRequest request)
    {
        var userId = HttpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var game = await _context.Games
            .Include(g => g.Units)
            .Include(g => g.NationStates)
            .Include(g => g.TerritoryStates)
            .Include(g => g.Players)
            .FirstOrDefaultAsync(g => g.Id == gameId);

        if (game == null) return NotFound();

        // 1. Validate Turn/Control
        var nation = game.CurrentTurnNation;
        var nationState = game.NationStates.First(n => n.Nation == nation);
        var controller = game.Players.FirstOrDefault(p => p.Id == nationState.ControllerId);
        
        if (controller == null || controller.UserId != userId) return Forbid();

        // 2. Validate Territory & Factory
        var territoryDef = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == request.TerritoryId);
        if (territoryDef == null) return BadRequest("Invalid territory.");
        
        var tState = game.TerritoryStates.FirstOrDefault(ts => ts.TerritoryId == request.TerritoryId);
        if (tState == null || !tState.HasFactory) return BadRequest("No factory here.");

        // 3. Identify Defender
        if (!territoryDef.Nation.HasValue) return BadRequest("Not a home province.");
        var defenderNation = territoryDef.Nation.Value;

        // 4. Validate Foreign (Attacker != Defender)
        if (nation == defenderNation) return BadRequest("Cannot destroy your own factory.");

        // 5. Check No Defenders
        bool hasDefenders = game.Units.Any(u => u.TerritoryId == request.TerritoryId && u.Nation == defenderNation);
        if (hasDefenders) return BadRequest("Cannot destroy factory while defenders are present.");

        // 6. Check 3 Armies provided
        if (request.UnitIds == null || request.UnitIds.Count != 3) return BadRequest("Must provide exactly 3 armies.");
        
        var attackingUnits = new List<Unit>();
        foreach (var uid in request.UnitIds)
        {
            var u = game.Units.FirstOrDefault(unit => unit.Id == uid);
            if (u == null) return BadRequest($"Unit {uid} not found.");
            if (u.Nation != nation) return BadRequest("Not your unit.");
            if (u.UnitType != UnitType.Army) return BadRequest("Must use armies.");
            if (u.TerritoryId != request.TerritoryId) return BadRequest("Army not in territory.");
            attackingUnits.Add(u);
        }

        // 7. Check Minimum Factory Exception (Defender must have > 1 factory)
        // We need to count how many factories the defender currently has
        // We look at TerritoryStates for this game where HasFactory is true AND it is a home province of defender
        // Note: We need to rely on TerritoryData for "Home Province" check
        var defenderFactoryCount = 0;
        foreach (var ts in game.TerritoryStates.Where(s => s.HasFactory))
        {
             var def = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == ts.TerritoryId);
             if (def != null && def.Nation == defenderNation)
             {
                 defenderFactoryCount++;
             }
        }

        if (defenderFactoryCount <= 1) return BadRequest("Cannot destroy the last factory of a nation.");

        // Execution
        // Remove Armies
        foreach (var u in attackingUnits)
        {
            _context.Units.Remove(u);
            game.Units.Remove(u);
        }

        // Remove Factory
        tState.HasFactory = false;

        LogAction(game, $"destroyed factory in {tState.TerritoryId}", "DestroyFactory", nation);

        await TryAutoAdvanceManeuver(game, nation);

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
            // Fix: Do NOT auto-resolve battles for Fleets. Fleet battles are handled by MoveFleet/Battle endpoint.
            // Coexisting fleets should survive to next phase.
            // Fix: Do NOT auto-resolve battles for Fleets OR Armies. 
            // Fleet/Army battles are handled by Move/Battle endpoint.
            // Coexisting units should survive to next phase.
            if (game.CurrentManeuverPhase != ManeuverPhase.Fleets && game.CurrentManeuverPhase != ManeuverPhase.Armies)
            {
                ResolveBattles(game, _context);
            }

            var oldPhase = game.CurrentManeuverPhase;

            // Advance Phase
            switch (game.CurrentManeuverPhase)
            {
                case ManeuverPhase.Fleets:
                    await UpdateTerritoryControl(game);
                    game.CurrentManeuverPhase = ManeuverPhase.Armies;
                    break;
                case ManeuverPhase.Armies:
                    await UpdateTerritoryControl(game);
                    game.CurrentManeuverPhase = ManeuverPhase.None; 
                    break;
                default:
                    return BadRequest("Invalid phase transition.");
            }
        
        LogAction(game, $"ended {oldPhase} maneuver phase", "NextPhase", nation);

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

    [HttpPost("{gameId}/battle-response")]
    public async Task<IActionResult> BattleResponse(Guid gameId, [FromBody] BattleResponseRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var game = await _context.Games
            .Include(g => g.NationStates)
            .Include(g => g.Players)
            .Include(g => g.Units)
            .Include(g => g.TerritoryStates)
            .FirstOrDefaultAsync(g => g.Id == gameId);

        if (game == null) return NotFound();
        if (game.PendingBattleTerritoryId == null || game.PendingBattleAggressorNation == null || !game.PendingBattleDefenders.Any())
        {
            return BadRequest("No pending battle.");
        }

        // Find the responding nation based on the user
        var respondingNations = game.NationStates
            .Where(ns => game.PendingBattleDefenders.Contains(ns.Nation))
            .Where(ns => ns.ControllerId != null && game.Players.Any(p => p.Id == ns.ControllerId && p.UserId == userId))
            .Select(ns => ns.Nation)
            .ToList();

        if (!respondingNations.Any()) return Forbid();

        // For simplicity, we process the first valid nation this user controls in the defenders list
        var respondingNation = respondingNations.First();

        if (request.IsFight)
        {
            // Fight triggers immediately!
            var territoryId = game.PendingBattleTerritoryId;
            var aggressorNation = game.PendingBattleAggressorNation.Value;

            // Resolve Battle between RespondingNation and AggressorNation
            var myUnits = game.Units.Where(u => u.TerritoryId == territoryId && u.Nation == respondingNation).ToList();
            var aggressorUnits = game.Units.Where(u => u.TerritoryId == territoryId && u.Nation == aggressorNation).ToList();

            // Destroy 1v1
            if (myUnits.Any() && aggressorUnits.Any())
            {
                var myUnit = myUnits.First();
                var aggUnit = aggressorUnits.First();
                _context.Units.Remove(myUnit);
                _context.Units.Remove(aggUnit);
                game.Units.Remove(myUnit);
                game.Units.Remove(aggUnit);

                LogAction(game, $"{respondingNation} chose FIGHT against {aggressorNation} in {territoryId}. Both units destroyed.", "BattleResponse", respondingNation);
            }

            // A single fight breaks the peace negotiation. Clear pending state.
            game.PendingBattleTerritoryId = null;
            game.PendingBattleAggressorNation = null;
            game.PendingBattleDefenders.Clear();
            
            await UpdateTerritoryControl(game);
            // Advance Maneuver if applicable
            await TryAutoAdvanceManeuver(game, aggressorNation);
        }
        else
        {
            // Peace! Remove from defenders list
            game.PendingBattleDefenders.Remove(respondingNation);
            LogAction(game, $"{respondingNation} agreed to PEACE with {game.PendingBattleAggressorNation} in {game.PendingBattleTerritoryId}.", "BattleResponse", respondingNation);

            if (!game.PendingBattleDefenders.Any())
            {
                // Everyone agreed to peace
                LogAction(game, $"All parties agreed to PEACE in {game.PendingBattleTerritoryId}.", "BattleResponse");
                var aggressorNation = game.PendingBattleAggressorNation.Value;
                
                game.PendingBattleTerritoryId = null;
                game.PendingBattleAggressorNation = null;

                await UpdateTerritoryControl(game);
                // Advance Maneuver if applicable
                await TryAutoAdvanceManeuver(game, aggressorNation);
            }
        }

        await _context.SaveChangesAsync();
        await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId);

        if (game.PendingBattleDefenders.Any())
        {
            _ = Task.Run(async () => { await Task.Delay(1500); await _botService.TryPlayBotTurnAsync(gameId); });
        }

        return Ok();
    }

    private async Task TryAutoAdvanceManeuver(Game game, Nation nation)
    {
        if (game.CurrentManeuverPhase == ManeuverPhase.Fleets)
        {
            var unmovedFleets = game.Units.Any(u => u.Nation == nation && u.UnitType == UnitType.Fleet && !u.HasMoved);
            if (!unmovedFleets)
            {
                await UpdateTerritoryControl(game);
                game.CurrentManeuverPhase = ManeuverPhase.Armies;
                LogAction(game, "auto-ended Fleets maneuver phase", "NextPhase", nation);
            }
        }

        if (game.CurrentManeuverPhase == ManeuverPhase.Armies)
        {
            var unmovedArmies = game.Units.Any(u => u.Nation == nation && u.UnitType == UnitType.Army && !u.HasMoved);
            if (!unmovedArmies)
            {
                await UpdateTerritoryControl(game);
                game.CurrentManeuverPhase = ManeuverPhase.None;
                LogAction(game, "auto-ended Armies maneuver phase", "NextPhase", nation);
            }
        }

        // If we advanced beyond Armies, and there's logic that needs to run, we log it.
        // Actually, no further changes needed since the caller will SaveChanges.
    }

    private async Task UpdateTerritoryControl(Game game)
    {
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
                        var oldController = tState.Controller;
                        tState.Controller = firstNation;
                        
                        string msg = oldController.HasValue 
                            ? $"took control of {territoryDef.Name} from {oldController.Value}"
                            : $"took control of {territoryDef.Name}";
                            
                        LogAction(game, msg, "FlagPlacement", firstNation);
                    }
                }
            }
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
        // Use GetRailReachableTerritories which already includes exit points
        var reachable = GetRailReachableTerritories(game, startId, nation);
        return reachable.Contains(endId);
    }
    private List<Unit>? GetConvoyFleets(Game game, string startId, string destId, Nation nation)
    {
        // 1. Identify all valid "Launch Points" (Current + Rail Reachable)
        var launchPoints = new HashSet<string>();
        launchPoints.Add(startId);
        // Only use rail for convoy embarkation if army starts on a rail-valid territory
        var startDef = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == startId);
        if (startDef != null && startDef.Type == TerritoryType.Land)
        {
            bool isStartHome = startDef.Nation == nation;
            var startState = game.TerritoryStates?.FirstOrDefault(ts => ts.TerritoryId == startId);
            bool isStartControlled = (isStartHome && (startState == null || startState.Controller == nation)) ||
                                     (startState != null && startState.Controller == nation);
            bool startHasHostiles = game.Units.Any(u => u.TerritoryId == startId && u.Nation != nation);

            if ((isStartHome || isStartControlled) && !startHasHostiles)
            {
                var railReachable = GetRailReachableTerritories(game, startId, nation, includeExitPoints: false);
                foreach (var r in railReachable) launchPoints.Add(r);
            }
        }

        // 2. BFS from Launch Points seeking Destination via Sea
        // State: (CurrentTerritory, UsedFleets)
        // Optimization: We only need to find *one* valid path.
        // But we need to track fleets to ensure we don't double-count or use used ones?
        // Actually, since we return on first success, tracking "Path" is enough.
        
        var queue = new Queue<(string Location, List<Unit> Fleets)>();
        var visited = new HashSet<string>();

        foreach (var lp in launchPoints)
        {
            // Optimization: If launch point IS destination (rail move), we handle it in Rail check? 
            // ManeuverController logic checks Rail first. So here destId is NOT reachable by rail.
            
            queue.Enqueue((lp, new List<Unit>()));
            visited.Add(lp);
        }

        while (queue.Count > 0)
        {
            var (currentId, currentFleets) = queue.Dequeue();

            // Neighbors
            if (!MapConnectivity.Adjacency.TryGetValue(currentId, out var neighbors)) continue;

            foreach (var neighbor in neighbors)
            {
                if (visited.Contains(neighbor)) continue;

                // Canal Check for Convoy Path (Sea <-> Sea or Land <-> Sea?)
                // Usually Convoy Path is Sea <-> Sea. 
                // But neighbors could be Land (Destination).
                // Check if (currentId -> neighbor) is a Canal Link.
                var canal = MapConnectivity.CanalLinks.FirstOrDefault(c => 
                    (c.Region1 == currentId && c.Region2 == neighbor) ||
                    (c.Region1 == neighbor && c.Region2 == currentId));

                if (canal != default)
                {
                   var controllerId = canal.ControllerId;
                   var tState = game.TerritoryStates.FirstOrDefault(ts => ts.TerritoryId == controllerId);
                   if (tState != null && tState.Controller != null && tState.Controller != nation)
                   {
                       // Canal Blocked
                       continue;
                   }
                }

                var neighborDef = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == neighbor);
                if (neighborDef == null) continue;

                if (neighborDef.Type == TerritoryType.Sea)
                {
                    // To enter/cross sea, we need an UNUSED fleet there
                    var fleet = game.Units.FirstOrDefault(u => 
                        u.Nation == nation && 
                        u.TerritoryId == neighbor && 
                        u.UnitType == UnitType.Fleet && 
                        !u.HasConvoyed &&
                        !currentFleets.Contains(u)); // Ensure we don't re-use same fleet instance in loop (BFS prevents loop but safe)

                    if (fleet != null)
                    {
                        var newFleets = new List<Unit>(currentFleets) { fleet };
                        visited.Add(neighbor);
                        queue.Enqueue((neighbor, newFleets));
                    }
                }
                else if (neighborDef.Type == TerritoryType.Land)
                {
                    // Potential Destination
                    if (neighbor == destId)
                    {
                        return currentFleets;
                    }
                    // Cannot pass through Land during Convoy (must end at Land)
                }
            }
        }

        return null;
    }

    private List<string> GetRailReachableTerritories(Game game, string startId, Nation nation, bool includeExitPoints = true)
    {
        var reachable = new HashSet<string>();
        // Queue stores (id, cost). Cost represents number of border crossings/non-rail steps.
        var queue = new Queue<(string id, int cost)>();
        // Visited stores min cost found so far to reach a territory
        var minCosts = new Dictionary<string, int>();

        // Start with cost 0 (Start node itself is always reachable)
        queue.Enqueue((startId, 0));
        minCosts[startId] = 0;

        while (queue.Count > 0)
        {
            var (currentId, currentCost) = queue.Dequeue();

            // Optimization: If we found a better path to this node already, skip
            if (minCosts.TryGetValue(currentId, out var recordedCost) && currentCost > recordedCost)
                continue;

            if (MapConnectivity.Adjacency.TryGetValue(currentId, out var neighbors))
            {
                var currentDef = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == currentId);
                bool isCurrentHome = currentDef?.Nation == nation;

                foreach (var neighborId in neighbors)
                {
                    var neighborDef = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == neighborId);
                    if (neighborDef == null || neighborDef.Type != TerritoryType.Land) continue;

                    // Determine Edge Cost
                    // Rail Step = (Current Is Home) AND (Neighbor Is Home) AND (Neighbor Is Safe/Controlled).
                    // If it's a Rail Step, Cost is 0.
                    // Otherwise (Border Crossing, Entry, Exit, or Hostile/Uncontrolled Home), Cost is 1.

                    bool isNeighborHome = neighborDef.Nation == nation;
                    
                    var tState = game.TerritoryStates?.FirstOrDefault(ts => ts.TerritoryId == neighborId);
                    // Fallback to Owner if Controller is null
                    var effectiveController = tState?.Controller ?? neighborDef.Nation;
                    bool isControlledByUs = effectiveController == nation;
                    bool hasHostileUnits = game.Units.Any(u => u.TerritoryId == neighborId && u.Nation != nation && u.UnitType == UnitType.Army);

                    bool isRailStep = false;
                    if (isCurrentHome && isNeighborHome && isControlledByUs && !hasHostileUnits)
                    {
                        isRailStep = true;
                    }

                    int edgeCost = isRailStep ? 0 : 1;
                    int newCost = currentCost + edgeCost;

                    // Helper logic: If includeExitPoints is False, we forbid ANY Non-Home destination?
                    // Previous logic: Forbidden Exit.
                    // New User Logic: Allow Exit (Cost 1).
                    // But if includeExitPoints is FALSE (convoy), maybe we enforce STRICT home?
                    // If includeExitPoints is FALSE, we should perhaps force newCost to be 0 for it to be valid?
                    // Or check isNeighborHome explicitly?
                    if (!includeExitPoints && !isNeighborHome)
                    {
                       // If we don't include exit points (e.g. Convoy validation?), we typically only want Rail Nodes.
                       // So skip Foreign.
                       continue;
                    }

                    if (newCost > 1) continue; // Cannot exceed 1 border crossing

                    // Add to Reachable
                    reachable.Add(neighborId);

                    // Add to Queue if better path
                    if (!minCosts.TryGetValue(neighborId, out var oldCost) || newCost < oldCost)
                    {
                        minCosts[neighborId] = newCost;
                        queue.Enqueue((neighborId, newCost));
                    }
                }
            }
        }
        return reachable.ToList();
    }

    private List<Unit>? ValidateSpecificConvoyFleets(Game game, string startId, string destId, Nation nation, List<Guid> fleetIds)
    {
        Console.WriteLine($"[ValidateSpecificConvoyFleets] Start={startId} Dest={destId} Fleets={fleetIds.Count}");
        // 1. Retrieve Fleets
        var fleets = new List<Unit>();
        foreach(var fId in fleetIds)
        {
            var f = game.Units.FirstOrDefault(u => u.Id == fId);
            if(f == null) { Console.WriteLine($"[ValidateSpecificConvoyFleets] Fleet {fId} Not Found"); return null; }
            if (f.Nation != nation) { Console.WriteLine($"[ValidateSpecificConvoyFleets] Fleet {fId} Wrong Nation"); return null; }
            if (f.UnitType != UnitType.Fleet) { Console.WriteLine($"[ValidateSpecificConvoyFleets] Fleet {fId} Not Fleet"); return null; }
            if (f.HasConvoyed) { Console.WriteLine($"[ValidateSpecificConvoyFleets] Fleet {fId} HasConvoyed=True"); return null; }
            fleets.Add(f);
        }

        // 2. Validate Chain (BFS restricted to ONLY these fleets)
        // Start from launch points
        var launchPoints = new HashSet<string>();
        launchPoints.Add(startId);
        // Only use rail for convoy embarkation if army starts on a rail-valid territory
        var startDef2 = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == startId);
        if (startDef2 != null && startDef2.Type == TerritoryType.Land)
        {
            bool isStartHome2 = startDef2.Nation == nation;
            var startState2 = game.TerritoryStates?.FirstOrDefault(ts => ts.TerritoryId == startId);
            // Fix: Strict Home Rule for consistency. Handle null controller by defaulting to Nation.
            var effectiveController2 = startState2?.Controller ?? startDef2.Nation;
            bool isControlledByUs = effectiveController2 == nation;
            
            bool startHasHostiles2 = game.Units.Any(u => u.TerritoryId == startId && u.Nation != nation);

            if (isStartHome2 && isControlledByUs && !startHasHostiles2)
            {
                var railReachable = GetRailReachableTerritories(game, startId, nation, includeExitPoints: false);
                foreach (var r in railReachable) launchPoints.Add(r);
            }
        }

        var queue = new Queue<string>();
        var visited = new HashSet<string>();
        
        foreach (var lp in launchPoints)
        {
            queue.Enqueue(lp);
            visited.Add(lp);
        }

        while(queue.Count > 0)
        {
            var currentId = queue.Dequeue();
            
            // Fix: Use direct Adjacency to see Sea neighbors
            if (!MapConnectivity.Adjacency.TryGetValue(currentId, out var neighbors)) continue;

            foreach(var neighbor in neighbors)
            {
                if(visited.Contains(neighbor)) continue;

                var tumor = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == neighbor);
                if (tumor == null) continue;

                if (tumor.Type == TerritoryType.Sea)
                {
                    // Must have one of the SPECIFIED fleets here
                    if (fleets.Any(f => f.TerritoryId == neighbor))
                    {
                        visited.Add(neighbor);
                        queue.Enqueue(neighbor);
                    }
                }
                else if (tumor.Type == TerritoryType.Land)
                {
                    if (neighbor == destId) return fleets;
                }
            }
        }

        Console.WriteLine("[ValidateSpecificConvoyFleets] Destination NOT reached");
        return null; // Chain broken or destination unreachable with these fleets
    }
    private void LogAction(Game game, string message, string type, Nation? nation = null)
    {
        var action = new GameAction
        {
            GameId = game.Id,
            Timestamp = DateTime.UtcNow,
            PlayerName = User.Identity?.Name ?? "System",
            Message = message,
            ActionType = type,
            Nation = nation
        };
        _context.GameActions.Add(action);
        // Note: Caller must SaveChanges
    }
}
