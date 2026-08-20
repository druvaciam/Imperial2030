using Imperial2030.Server.Data;
using Imperial2030.Server.Models;
using Imperial2030.Server.Helpers;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using Imperial2030.Server.Helpers;
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

    /// <summary>
    /// When true, suppresses all SignalR broadcasts from this controller instance. Set by
    /// GameReplayService while replaying actions (e.g. during ImportGame) so a large replay doesn't
    /// spam every connected browser with GameUpdated/etc. events for a game they can't see yet.
    /// </summary>
    public bool SuppressBroadcasts { get; set; } = false;

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
            .AsSplitQuery()
            .FirstOrDefaultAsync(g => g.Id == gameId);

        if (game == null) return NotFound();
        if (game.Status != GameStatus.InProgress) return BadRequest("Game not in progress.");
        if (game.CurrentManeuverPhase != ManeuverPhase.Fleets) return BadRequest("Not in Fleet Maneuver phase.");
        if (game.PendingBattleDefenders.Any()) return BadRequest("Cannot move fleets while a battle is pending.");

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

        // Captured before any mutation below — this is the unit's hostility at its ORIGIN territory,
        // used by GameReplayService to disambiguate which specific unit moved when several otherwise-
        // identical units (same Nation/UnitType/FromTerritory) sit at the same origin.
        bool sourceWasHostile = unit.IsHostile;

        if (unit.TerritoryId == request.DestinationId)
        {
            unit.HasMoved = true;
            _context.Entry(unit).State = EntityState.Modified;
            GameLogger.LogUnitMove(_context, game, unit.UnitType, sourceWasHostile, unit.TerritoryId, request.DestinationId, false, nation, controller.GetPlayerName(_context));
            await _context.SaveChangesAsync();
            return Ok();
        }

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

        // Determine if the fleet will engage in battle
        bool willFight = false;
        // Set true by the auto-resolve branches below, which already destroy the mover and log both its
        // move and the resulting Battle themselves — without this guard the unconditional logging block
        // further down (which exists for the normal non-battle move case) fires AGAIN for the same move,
        // producing a second, spurious log entry for a unit that no longer exists. Harmless for live
        // rendering (nothing reconstructs state from the log), but replay (GameReplayService) takes the
        // log literally and fails trying to move a unit that was destroyed in the battle it just replayed.
        bool moveAlreadyLogged = false;
        var destDefForBattle = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == request.DestinationId);
        bool isForeignHome = destDefForBattle != null && destDefForBattle.Nation.HasValue && destDefForBattle.Nation.Value != nation;
        bool isMyHome = destDefForBattle != null && destDefForBattle.Nation.HasValue && destDefForBattle.Nation.Value == nation;

        var friendlyNations = controller != null
            ? game.NationStates.Where(n => n.ControllerId == controller.Id).Select(n => n.Nation).ToHashSet()
            : new HashSet<Nation>();
        friendlyNations.Add(nation);

        List<Nation> foreignFleets = game.Units
            .Where(u => u.TerritoryId == request.DestinationId && !friendlyNations.Contains(u.Nation))
            .Where(u => u.UnitType == UnitType.Fleet || (isForeignHome && u.Nation == destDefForBattle!.Nation!.Value && request.IsHostile))
            .Select(u => u.Nation)
            .Distinct()
            .ToList();

        if (request.IsHostile)
        {
            if (request.BattleTargetNation.HasValue)
            {
                willFight = game.Units.Any(u => u.TerritoryId == request.DestinationId && u.Nation == request.BattleTargetNation.Value);
            }
            else
            {
                willFight = foreignFleets.Any();
            }
        }

        if (request.IsHostile && !willFight)
        {
            var destDef2 = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == request.DestinationId);
            if (destDef2 != null && destDef2.Nation.HasValue && destDef2.Nation.Value != nation)
            {
                var tState = game.TerritoryStates.FirstOrDefault(ts => ts.TerritoryId == request.DestinationId);
                if (tState != null && tState.HasFactory)
                {
                    var defenderNation = destDef2.Nation.Value;
                    var defenderFactoryCount = game.TerritoryStates.Count(s =>
                    {
                        if (!s.HasFactory) return false;
                        var t = TerritoryData.AllTerritories.FirstOrDefault(td => td.Id == s.TerritoryId);
                        if (t == null || t.Nation != defenderNation) return false;
                        bool isOccupied = game.Units.Any(u => u.Id != unit.Id && u.TerritoryId == s.TerritoryId && u.Nation != defenderNation && u.IsHostile);
                        return !isOccupied;
                    });
                    bool isTargetOccupied = game.Units.Any(u => u.Id != unit.Id && u.TerritoryId == request.DestinationId && u.Nation != defenderNation && u.IsHostile);
                    if (defenderFactoryCount <= 1 && !isTargetOccupied)
                    {
                        return BadRequest("Cannot enter the last unoccupied factory of a nation hostilely. Must enter peacefully.");
                    }
                }
            }
        }

        // Auto-Battle Logic (If specified)
        // Apply hostility choice
        unit.IsHostile = request.IsHostile;

        if (request.BattleTargetNation.HasValue)
        {
            var targetNation = request.BattleTargetNation.Value;
            // Find a fleet of target nation in destination
            // BattleTargetUnitType pins the exact unit type when set. Only GameReplayService ever sets it
            // (from the already-logged Battle action's own DefenderUnitType, looked up ahead of time) —
            // live play never populates it, so this branch's original any-unit candidate set is unchanged
            // for real games. See the matching comment on MoveArmy's equivalent branch.
            var enemyFleet = request.BattleTargetUnitType.HasValue
                ? game.Units.FirstOrDefault(u => u.TerritoryId == request.DestinationId && u.Nation == targetNation && u.UnitType == request.BattleTargetUnitType.Value)
                : game.Units.FirstOrDefault(u => u.TerritoryId == request.DestinationId && u.Nation == targetNation);

            if (enemyFleet != null)
            {
                GameLogger.LogUnitMove(_context, game, unit.UnitType, sourceWasHostile, sourceTerritory, request.DestinationId, true, nation, controller.GetPlayerName(_context));
                // Destroy Both
                _context.Units.Remove(unit);
                _context.Units.Remove(enemyFleet);
                game.Units.Remove(unit);
                game.Units.Remove(enemyFleet);
                GameLogger.LogBattleDestruction(_context, game, unit.UnitType, targetNation, enemyFleet.UnitType, request.DestinationId, nation, controller.GetPlayerName(_context));
                moveAlreadyLogged = true;
            }
        }
        else
        {
            if (isMyHome && foreignFleets.Any())
            {
                request.IsHostile = true;
                unit.IsHostile = true;
            }

            // Peace Move or Hostile Move for Fleets
            if (foreignFleets.Any() && request.IsHostile)
            {
                if (request.IsHostile && foreignFleets.Count == 1)
                {
                    // Auto-resolve hostile battle if there is only 1 valid target
                    var targetNation = foreignFleets.First();
                    var enemyFleet = game.Units.FirstOrDefault(u =>
                        u.TerritoryId == request.DestinationId &&
                        u.Nation == targetNation &&
                        (u.UnitType == UnitType.Fleet || (isForeignHome && u.Nation == destDefForBattle.Nation.Value && request.IsHostile)));

                    if (enemyFleet != null)
                    {
                        GameLogger.LogUnitMove(_context, game, unit.UnitType, sourceWasHostile, sourceTerritory, request.DestinationId, true, nation, controller.GetPlayerName(_context));
                        // Destroy Both
                        _context.Units.Remove(unit);
                        _context.Units.Remove(enemyFleet);
                        game.Units.Remove(unit);
                        game.Units.Remove(enemyFleet);
                        GameLogger.LogBattleDestruction(_context, game, unit.UnitType, targetNation, enemyFleet.UnitType, request.DestinationId, nation, controller.GetPlayerName(_context));
                        moveAlreadyLogged = true;
                    }
                }
                else
                {
                    // Enter Pending Battle Negotiation Phase
                    game.PendingBattleTerritoryId = request.DestinationId;
                    game.PendingBattleAggressorNation = nation;
                    game.PendingBattleDefenders = foreignFleets.ToList();

                    string peaceOrHostile = request.IsHostile ? "hostilely" : "peacefully";
                    GameLogger.LogUnitMoveAwaitingResponse(_context, game, UnitType.Fleet, sourceWasHostile, sourceTerritory, request.DestinationId, unit.IsHostile, string.Join(", ", foreignFleets), nation, controller.GetPlayerName(_context));
                }
            }
        }

        if (!game.PendingBattleDefenders.Any())
        {
            // Only log standard move and TryAutoAdvance if there's no pending battle blocking the phase.
            // If Pending, advancement and standard logging is delayed.
            if (!moveAlreadyLogged)
            {
                GameLogger.LogUnitMove(_context, game, UnitType.Fleet, sourceWasHostile, sourceTerritory, request.DestinationId, unit.IsHostile, nation, controller.GetPlayerName(_context));
            }
            await UpdateTerritoryControl(game);
            await TryAutoAdvanceManeuver(game, nation);
        }
        await UpdateTerritoryControl(game);
        await _context.SaveChangesAsync();
        if (!SuppressBroadcasts) { await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId); }

        if (game.PendingBattleDefenders.Any())
        {
            _botService.TriggerBotTurn(gameId);
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
            .AsSplitQuery()
            .FirstOrDefaultAsync(g => g.Id == gameId);

        if (game == null) return NotFound();
        if (game.PendingBattleDefenders.Any()) return BadRequest("Cannot initiate battles while another battle is pending.");

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

        GameLogger.LogBattleDestruction(_context, game, unit.UnitType, targetNation, enemyUnit.UnitType, unit.TerritoryId, nation, controller.GetPlayerName(_context));

        await UpdateTerritoryControl(game);
        await TryAutoAdvanceManeuver(game, nation);

        await _context.SaveChangesAsync();
        if (!SuppressBroadcasts) { await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId); }

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
            .AsSplitQuery()
            .FirstOrDefaultAsync(g => g.Id == gameId);

        if (game == null) return NotFound();
        if (game.Status != GameStatus.InProgress) return BadRequest("Game not in progress.");
        if (game.CurrentManeuverPhase != ManeuverPhase.Armies) return BadRequest("Not in Army Maneuver phase.");
        if (game.PendingBattleDefenders.Any()) return BadRequest("Cannot move armies while a battle is pending.");

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

        // Captured before any mutation below — this is the unit's hostility at its ORIGIN territory,
        // used by GameReplayService to disambiguate which specific unit moved when several otherwise-
        // identical units (same Nation/UnitType/FromTerritory) sit at the same origin.
        bool sourceWasHostile = unit.IsHostile;

        if (unit.TerritoryId == request.DestinationId)
        {
            unit.HasMoved = true;
            _context.Entry(unit).State = EntityState.Modified;
            GameLogger.LogUnitMove(_context, game, unit.UnitType, sourceWasHostile, unit.TerritoryId, request.DestinationId, false, nation, controller.GetPlayerName(_context));
            await _context.SaveChangesAsync();
            return Ok();
        }

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
            if (Imperial2030.Server.Helpers.ManeuverHelper.CanMoveByRail(game, unit.TerritoryId, request.DestinationId, nation))
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
                    usedFleets = Imperial2030.Server.Helpers.ManeuverHelper.ValidateSpecificConvoyFleets(game, unit.TerritoryId, request.DestinationId, nation, request.ConvoyFleetIds);
                    if (usedFleets == null)
                    {
                        return BadRequest("Invalid convoy path with specified fleets.");
                    }
                }
                else
                {
                    // Auto-select fleets
                    usedFleets = Imperial2030.Server.Helpers.ManeuverHelper.GetConvoyFleets(game, unit.TerritoryId, request.DestinationId, nation);
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

        // Determine if the unit will engage in battle (and thus be destroyed)
        bool willFight = false;
        // See the matching comment in MoveFleet — guards against double-logging the same move when an
        // auto-resolve battle branch below already logged it (and the battle) before destroying the unit.
        bool moveAlreadyLogged = false;
        var destDefForBattle = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == request.DestinationId);
        bool isForeignHome = destDefForBattle != null && destDefForBattle.Nation.HasValue && destDefForBattle.Nation.Value != nation;
        bool isMyHome = destDefForBattle != null && destDefForBattle.Nation.HasValue && destDefForBattle.Nation.Value == nation;

        var friendlyNations = controller != null
            ? game.NationStates.Where(n => n.ControllerId == controller.Id).Select(n => n.Nation).ToHashSet()
            : new HashSet<Nation>();
        friendlyNations.Add(nation);

        // Always compute foreignDefenders so forced-battle logic (isMyHome) works even on peaceful moves
        List<Nation> foreignDefenders = game.Units
            .Where(u => u.TerritoryId == request.DestinationId && !friendlyNations.Contains(u.Nation))
            .Where(u => u.UnitType == UnitType.Army || (isForeignHome && u.Nation == destDefForBattle!.Nation!.Value && request.IsHostile))
            .Select(u => u.Nation)
            .Distinct()
            .ToList();

        if (request.IsHostile)
        {
            if (request.BattleTargetNation.HasValue)
            {
                willFight = game.Units.Any(u => u.TerritoryId == request.DestinationId && u.Nation == request.BattleTargetNation.Value);
            }
            else
            {
                willFight = foreignDefenders.Any();
            }
        }

        if (request.IsHostile && !willFight)
        {
            var destDef2 = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == request.DestinationId);
            if (destDef2 != null && destDef2.Nation.HasValue && destDef2.Nation.Value != nation)
            {
                var tState = game.TerritoryStates.FirstOrDefault(ts => ts.TerritoryId == request.DestinationId);
                if (tState != null && tState.HasFactory)
                {
                    var defenderNation = destDef2.Nation.Value;
                    var defenderFactoryCount = game.TerritoryStates.Count(s =>
                    {
                        if (!s.HasFactory) return false;
                        var t = TerritoryData.AllTerritories.FirstOrDefault(td => td.Id == s.TerritoryId);
                        if (t == null || t.Nation != defenderNation) return false;
                        bool isOccupied = game.Units.Any(u => u.Id != unit.Id && u.TerritoryId == s.TerritoryId && u.Nation != defenderNation && u.IsHostile);
                        return !isOccupied;
                    });

                    bool isTargetOccupied = game.Units.Any(u => u.Id != unit.Id && u.TerritoryId == request.DestinationId && u.Nation != defenderNation && u.IsHostile);

                    if (defenderFactoryCount <= 1 && !isTargetOccupied)
                    {
                        return BadRequest("Cannot enter the last unoccupied factory of a nation hostilely. Must enter peacefully.");
                    }
                }
            }
        }

        // Auto-Battle Logic (MoveArmy)
        // Apply hostility choice
        unit.IsHostile = request.IsHostile;

        if (request.BattleTargetNation.HasValue)
        {
            var targetNation = request.BattleTargetNation.Value;
            // BattleTargetUnitType pins the exact unit type when set — only GameReplayService ever sets it
            // (from the already-logged Battle action's own DefenderUnitType), so live play (which never
            // populates it) keeps this branch's original any-unit candidate set unchanged.
            var enemyUnit = request.BattleTargetUnitType.HasValue
                ? game.Units.FirstOrDefault(u => u.TerritoryId == request.DestinationId && u.Nation == targetNation && u.UnitType == request.BattleTargetUnitType.Value)
                : game.Units.FirstOrDefault(u => u.TerritoryId == request.DestinationId && u.Nation == targetNation);

            if (enemyUnit != null)
            {
                GameLogger.LogUnitMove(_context, game, unit.UnitType, sourceWasHostile, sourceTerritory, request.DestinationId, true, nation, controller.GetPlayerName(_context));
                // Destroy Both
                _context.Units.Remove(unit);
                _context.Units.Remove(enemyUnit);
                game.Units.Remove(unit);
                game.Units.Remove(enemyUnit);
                GameLogger.LogBattleDestruction(_context, game, unit.UnitType, targetNation, enemyUnit.UnitType, request.DestinationId, nation, controller.GetPlayerName(_context));
                moveAlreadyLogged = true;
            }
        }
        else
        {
            if (isMyHome && foreignDefenders.Any())
            {
                // Foreign armies in your home territory are always hostile. You cannot peacefully coexist.
                request.IsHostile = true;
                unit.IsHostile = true;
            }

            if (foreignDefenders.Any())
            {
                if (request.IsHostile && foreignDefenders.Count == 1)
                {
                    // Auto-resolve hostile battle if there is only 1 valid target
                    var targetNation = foreignDefenders.First();
                    var enemyUnit = game.Units.FirstOrDefault(u =>
                        u.TerritoryId == request.DestinationId &&
                        u.Nation == targetNation &&
                        (u.UnitType == UnitType.Army || (isForeignHome && u.Nation == destDefForBattle.Nation.Value && request.IsHostile)));

                    if (enemyUnit != null)
                    {
                        GameLogger.LogUnitMove(_context, game, unit.UnitType, sourceWasHostile, sourceTerritory, request.DestinationId, true, nation, controller.GetPlayerName(_context));
                        // Destroy Both
                        _context.Units.Remove(unit);
                        _context.Units.Remove(enemyUnit);
                        game.Units.Remove(unit);
                        game.Units.Remove(enemyUnit);
                        GameLogger.LogBattleDestruction(_context, game, unit.UnitType, targetNation, enemyUnit.UnitType, request.DestinationId, nation, controller.GetPlayerName(_context));
                        moveAlreadyLogged = true;
                    }
                }
                else
                {
                    // Enter Pending Battle Negotiation Phase (if peaceful or multiple targets)
                    game.PendingBattleTerritoryId = request.DestinationId;
                    game.PendingBattleAggressorNation = nation;
                    game.PendingBattleDefenders = foreignDefenders.ToList();

                    string peaceOrHostile = request.IsHostile ? "hostilely" : "peacefully";
                    GameLogger.LogUnitMoveAwaitingResponse(_context, game, UnitType.Army, sourceWasHostile, sourceTerritory, request.DestinationId, unit.IsHostile, string.Join(", ", foreignDefenders), nation, controller.GetPlayerName(_context));
                }
            }
        }

        if (!game.PendingBattleDefenders.Any())
        {
            if (!moveAlreadyLogged)
            {
                GameLogger.LogUnitMove(_context, game, UnitType.Army, sourceWasHostile, sourceTerritory, request.DestinationId, unit.IsHostile, nation, controller.GetPlayerName(_context));
            }
            await UpdateTerritoryControl(game);
            await TryAutoAdvanceManeuver(game, nation);
        }
        await UpdateTerritoryControl(game);
        await _context.SaveChangesAsync();
        if (!SuppressBroadcasts) { await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId); }

        if (game.PendingBattleDefenders.Any())
        {
            _botService.TriggerBotTurn(gameId);
        }

        return Ok();
    }

    [HttpPost("{gameId}/toggle-hostility/{unitId}")]
    public async Task<IActionResult> ToggleHostility(Guid gameId, Guid unitId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var game = await _context.Games
            .Include(g => g.Units)
            .Include(g => g.NationStates)
            .Include(g => g.Players)
            .AsSplitQuery()
            .FirstOrDefaultAsync(g => g.Id == gameId);

        if (game == null) return NotFound();
        if (game.Status != GameStatus.InProgress) return BadRequest("Game not in progress.");
        if (game.CurrentManeuverPhase == ManeuverPhase.None) return BadRequest("Not in Maneuver phase.");

        var unit = game.Units.FirstOrDefault(u => u.Id == unitId);
        if (unit == null) return NotFound("Unit not found.");

        var nation = game.CurrentTurnNation;
        var nationState = game.NationStates.First(n => n.Nation == nation);
        if (nationState.ControllerId == null) return BadRequest("No controller for this nation.");

        var controller = game.Players.First(p => p.Id == nationState.ControllerId);
        if (controller.UserId != userId) return Forbid();

        if (unit.Nation != nation) return BadRequest("You can only toggle hostility of your own units.");

        unit.IsHostile = !unit.IsHostile;

        GameLogger.LogHostilityToggle(_context, game, unit.UnitType, unit.TerritoryId, unit.IsHostile, nation, controller.GetPlayerName(_context));

        await _context.SaveChangesAsync();
        if (!SuppressBroadcasts) { await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId); }

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
            .AsSplitQuery()
            .FirstOrDefaultAsync(g => g.Id == gameId);

        if (game == null) return NotFound();
        if (game.PendingBattleDefenders.Any()) return BadRequest("Cannot destroy factories while a battle is pending.");

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

        // 6. Check the required number of armies were provided
        if (request.UnitIds == null || request.UnitIds.Count != ManeuverRules.DestroyFactoryArmyCost) return BadRequest($"Must provide exactly {ManeuverRules.DestroyFactoryArmyCost} armies.");

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

        GameLogger.LogFactoryDestruction(_context, game, tState.TerritoryId, nation, controller.GetPlayerName(_context));

        await TryAutoAdvanceManeuver(game, nation);

        await _context.SaveChangesAsync();
        if (!SuppressBroadcasts) { await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId); }

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
                 .AsSplitQuery()
                 .FirstOrDefaultAsync(g => g.Id == gameId);

            if (game == null) return NotFound();
            if (game.PendingBattleDefenders.Any()) return BadRequest("Cannot advance phase while a battle is pending.");

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

            string playerName = game.Players.FirstOrDefault(p => p.Id == game.ActingPlayerId).GetPlayerName(_context);
            GameLogger.LogEndManeuverPhase(_context, game, oldPhase.ToString(), nation, playerName);

            await _context.SaveChangesAsync();
            if (!SuppressBroadcasts) { await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId); }

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
            .AsSplitQuery()
            .FirstOrDefaultAsync(g => g.Id == gameId);

        if (game == null) return NotFound();
        if (game.PendingBattleTerritoryId == null || game.PendingBattleAggressorNation == null || !game.PendingBattleDefenders.Any())
        {
            return BadRequest("No pending battle.");
        }

        // Find the responding nation based on the user
        var respondingNations = game.NationStates
            .Where(ns => game.PendingBattleDefenders.Contains(ns.Nation))
            .Where(ns => (ns.ControllerId != null && game.Players.Any(p => p.Id == ns.ControllerId && p.UserId == userId)) || ns.ControllerId == null)
            .Select(ns => ns.Nation)
            .ToList();

        if (!respondingNations.Any()) return Forbid();

        // Process the specific requested nation or fallback to first valid nation
        var respondingNation = (request.Nation.HasValue && respondingNations.Contains(request.Nation.Value))
            ? request.Nation.Value
            : respondingNations.First();

        // User.Identity?.Name is never populated by GameReplayService's replay auth context (it only sets
        // NameIdentifier), so "?? System"/"?? Human" silently fired on every replay, poisoning the logged
        // PlayerName with a value that can never re-resolve to a real Player on a later replay of this
        // game's own log. GetPlayerName resolves the actual controlling player/bot, same as the other
        // logging call sites in this controller.
        var respondingController = game.Players.FirstOrDefault(p => p.Id == game.NationStates.First(ns => ns.Nation == respondingNation).ControllerId);
        string respondingPlayerName = respondingController?.GetPlayerName(_context) ?? GameConstants.SystemPlayerName;

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

                GameLogger.LogBattleResponseDestruction(_context, game, respondingNation, myUnit.UnitType, aggressorNation, aggUnit.UnitType, territoryId, respondingPlayerName);
                if (!SuppressBroadcasts) { await _hubContext.Clients.Group(gameId.ToString()).SendAsync("ShowToast", ToastBuilder.BuildBattleResponseToast(respondingNation, aggressorNation, isFight: true), false); }
            }

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
            var defenders = game.PendingBattleDefenders.ToList();
            defenders.Remove(respondingNation);
            game.PendingBattleDefenders = defenders;
            _context.Entry(game).Property(g => g.PendingBattleDefenders).IsModified = true;
            GameLogger.LogBattleResponsePeace(_context, game, respondingNation, game.PendingBattleAggressorNation.Value, game.PendingBattleTerritoryId, respondingPlayerName);
            if (!SuppressBroadcasts) { await _hubContext.Clients.Group(gameId.ToString()).SendAsync("ShowToast", ToastBuilder.BuildBattleResponseToast(respondingNation, respondingNation, isFight: false), false); }

            var territoryId = game.PendingBattleTerritoryId;
            var aggressorNation = game.PendingBattleAggressorNation.Value;

            if (!game.PendingBattleDefenders.Any() || !game.Units.Any(u => u.TerritoryId == territoryId && u.Nation == aggressorNation))
            {
                // Everyone agreed to peace or aggressor eliminated
                GameLogger.LogAllPartiesPeace(_context, game, territoryId, GameConstants.SystemPlayerName);

                game.PendingBattleTerritoryId = null;
                game.PendingBattleAggressorNation = null;
                game.PendingBattleDefenders.Clear();

                await UpdateTerritoryControl(game);
                // Advance Maneuver if applicable
                await TryAutoAdvanceManeuver(game, aggressorNation);
            }
        }
        await UpdateTerritoryControl(game);
        await _context.SaveChangesAsync();
        if (!SuppressBroadcasts) { await _hubContext.Clients.Group(gameId.ToString()).SendAsync("GameUpdated", gameId); }

        if (game.PendingBattleDefenders.Any())
        {
            _botService.TriggerBotTurn(gameId);
        }

        return Ok();
    }

    // The player who controls `nation` — the actor a maneuver-phase event belongs to. Deliberately NOT
    // game.ActingPlayerId: that's only populated during an Investor phase, so during a maneuver phase it's
    // null and Players.FirstOrDefault(...) returned null, which GetPlayerName renders as the literal
    // "Unknown" (visible in the command log as `Unknown USA auto-ended Armies maneuver phase`). Affects live
    // play too, not just replay.
    private string ResolveNationControllerName(Game game, Nation nation)
    {
        var controllerId = game.NationStates.FirstOrDefault(ns => ns.Nation == nation)?.ControllerId;
        var controller = controllerId.HasValue ? game.Players.FirstOrDefault(p => p.Id == controllerId.Value) : null;
        return controller?.GetPlayerName(_context) ?? GameConstants.SystemPlayerName;
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
                GameLogger.LogAutoEndManeuverPhase(_context, game, "Fleets", nation, ResolveNationControllerName(game, nation));
            }
        }

        if (game.CurrentManeuverPhase == ManeuverPhase.Armies)
        {
            var unmovedArmies = game.Units.Any(u => u.Nation == nation && u.UnitType == UnitType.Army && !u.HasMoved);
            if (!unmovedArmies)
            {
                await UpdateTerritoryControl(game);
                game.CurrentManeuverPhase = ManeuverPhase.None;
                GameLogger.LogAutoEndManeuverPhase(_context, game, "Armies", nation, ResolveNationControllerName(game, nation));
            }
        }

        // If we advanced beyond Armies, and there's logic that needs to run, we log it.
        // Actually, no further changes needed since the caller will SaveChanges.
    }

    public async Task UpdateTerritoryControl(Game game)
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
                    // Use in-memory collection instead of N+1 database queries
                    var states = game.TerritoryStates.Where(ts => ts.TerritoryId == tId).ToList();
                    var tState = states.FirstOrDefault();

                    if (states.Count > 1)
                    {
                        // Clean up duplicates caused by concurrent API calls
                        for (int i = 1; i < states.Count; i++)
                        {
                            game.TerritoryStates.Remove(states[i]);
                            _context.TerritoryStates.Remove(states[i]);
                        }
                    }

                    if (tState == null)
                    {
                        tState = new TerritoryState { TerritoryId = tId, GameId = game.Id };
                        game.TerritoryStates.Add(tState);
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
                        int flagCount = game.TerritoryStates.Count(ts => ts.Controller == firstNation);
                        // This helper runs for whichever nation's units triggered it, not necessarily the
                        // caller's own — User.Identity?.Name is both wrong in spirit here and, during
                        // replay, always null (GameReplayService's auth context only sets NameIdentifier),
                        // so it silently mislogged every automatic flag placement as "System".
                        var firstNationControllerId = game.NationStates.FirstOrDefault(ns => ns.Nation == firstNation)?.ControllerId;
                        var firstNationPlayerName = game.Players.FirstOrDefault(p => p.Id == firstNationControllerId)?.GetPlayerName(_context) ?? GameConstants.SystemPlayerName;

                        if (flagCount >= 15)
                        {
                            if (oldController != null)
                            {
                                tState.Controller = null;
                                GameLogger.LogTerritoryControlChange(_context, game, territoryDef.Name, oldController, null, firstNationPlayerName);
                            }
                        }
                        else
                        {
                            tState.Controller = firstNation;
                            GameLogger.LogTerritoryControlChange(_context, game, territoryDef.Name, oldController, firstNation, firstNationPlayerName);
                        }
                    }
                }
            }
        }
    }

    private void ResolveBattles(Game game, ApplicationDbContext context)
    {
        var activeNation = game.CurrentTurnNation;

        var potentialBattlegrounds = game.Units
            .Where(u => u.Nation == activeNation)
            .Select(u => u.TerritoryId)
            .Distinct()
            .ToList();

        foreach (var tId in potentialBattlegrounds)
        {
            var unitsInTerritory = game.Units.Where(u => u.TerritoryId == tId).ToList();

            var activeUnits = unitsInTerritory.Where(u => u.Nation == activeNation).ToList();
            var hostileUnits = unitsInTerritory.Where(u => u.Nation != activeNation && u.IsHostile).ToList();

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
            }
        }
    }
}
