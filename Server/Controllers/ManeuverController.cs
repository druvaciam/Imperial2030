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
// Every action here is a game move, so the whole controller refuses guests - GamesController makes the
// same refusal per-action because some of its endpoints are deliberately guest-readable.
[Authorize(Policy = GameConstants.NotGuestPolicy)]
public class ManeuverController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IHubContext<Imperial2030.Server.Hubs.GameHub> _hubContext;
    private readonly Imperial2030.Server.Services.BotService _botService;
    private readonly ILogger<ManeuverController> _logger;

    /// <summary>
    /// When true, suppresses all SignalR broadcasts from this controller instance. Set by
    /// GameReplayService while replaying actions (e.g. during ImportGame) so a large replay doesn't
    /// spam every connected browser with GameUpdated/etc. events for a game they can't see yet.
    /// </summary>
    public bool SuppressBroadcasts { get; set; } = false;

    // logger is optional so the many direct `new ManeuverController(...)` constructions in Tests/ keep
    // working; DI supplies the real one in production.
    public ManeuverController(ApplicationDbContext context, IHubContext<Imperial2030.Server.Hubs.GameHub> hubContext, Imperial2030.Server.Services.BotService botService, ILogger<ManeuverController>? logger = null)
    {
        _context = context;
        _hubContext = hubContext;
        _botService = botService;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ManeuverController>.Instance;
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
                .ThenInclude(p => p.User)
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

        // Imperial-2030-Rules.pdf p.8: "After their production or their import, fleets stay in the harbor.
        // Consequently, their first move is always to the sea region that is adjacent to the harbor. Once
        // fleets are at sea, they cannot return to a land region."
        //
        // So a fleet's destination is always a sea region — leaving its own harbour is the only move that
        // starts on land, and it still ends at sea. This previously rejected only land-to-land, which let
        // a fleet sail out of the North Atlantic and into Berlin or London. BotService and
        // TcpTrainingServer already filter fleet destinations to sea regions; this brings the human
        // endpoint in line with them and with the rulebook.
        //
        // Staying put is handled earlier and is unaffected ("As an alternative, any fleet may stay where
        // it is.").
        if (destT.Type != TerritoryType.Sea)
            return BadRequest("Fleets can only move into sea regions.");

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

        // Now a no-op for fleets: the destination is always a sea region (checked above), which has no
        // owning nation, so the helper returns false. Kept rather than deleted so the guard is still in
        // place if fleets are ever allowed onto land again — it costs one call and states the intent.
        if (request.IsHostile && !willFight
            && ManeuverHelper.IsProtectedLastFactoryProvince(game, nation, request.DestinationId, unit.Id))
        {
            return BadRequest("Cannot enter the last unoccupied factory of a nation hostilely. Must enter peacefully.");
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
                    game.PendingBattleAggressorUnitId = unit.Id;
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
            // No UpdateTerritoryControl here: flags are step 3 of the maneuver, placed once the fleets and
            // armies have all moved (Imperial-2030-Rules.pdf p.8/p.10) - see TryAutoAdvanceManeuver.
            await TryAutoAdvanceManeuver(game, nation);
        }
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
            // Required: destroying the last unmoved unit here ends the maneuver phase via
            // TryAutoAdvanceManeuver, whose flag pass reads and writes game.TerritoryStates. Without this
            // the collection loads empty, so every already-flagged region looks unflagged and gets a
            // duplicate TerritoryState row plus a spurious flag-placement log entry.
            .Include(g => g.TerritoryStates)
            .Include(g => g.Players)
                .ThenInclude(p => p.User)
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

        // See MoveFleet: a battle resolved mid-maneuver doesn't place flags either. The rulebook's
        // "as the result of a battle... the previous flag is removed and replaced" (p.10, 3. Flags) is
        // still settled in step 3, once every unit has finished moving.
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
                .ThenInclude(p => p.User)
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

        // Everything the army passes through on the way, for the log. Stays null for a plain step to an
        // adjacent territory, which has nothing in between.
        List<string>? routeVia = null;

        // Naming fleets is an explicit instruction to go by sea, so it overrides the mode the rules
        // would otherwise pick. Rail and convoy can both reach the same pair of territories, and
        // silently railing an army whose carrier fleets the caller chose is wrong twice over: the
        // player asked for a convoy, and a replay feeding a logged convoy back in would reproduce it as
        // a rail move and record a different journey than the one that happened.
        bool convoyRequested = request.ConvoyFleetIds != null && request.ConvoyFleetIds.Any();

        // Adjacent step, else rail, else convoy - see ManeuverHelper.DetermineArmyMoveMode. The bot's
        // maneuver asks the same helper, so the two cannot drift apart again.
        var moveMode = convoyRequested
            ? Imperial2030.Server.Helpers.ManeuverHelper.ArmyMoveMode.Convoy
            : Imperial2030.Server.Helpers.ManeuverHelper.DetermineArmyMoveMode(game, unit.TerritoryId, request.DestinationId, nation);

        if (moveMode != Imperial2030.Server.Helpers.ManeuverHelper.ArmyMoveMode.AdjacentStep)
        {
            // Check Rail Logic
            if (moveMode == Imperial2030.Server.Helpers.ManeuverHelper.ArmyMoveMode.Rail)
            {
                // Move valid by Rail - No extra cost/side effects for now
                routeVia = Imperial2030.Server.Helpers.ManeuverHelper.BuildMoveRoute(game, unit.TerritoryId, request.DestinationId, nation, moveMode, null);
            }
            else
            {
                // Check Convoy Logic
                List<Unit>? usedFleets = null;

                if (convoyRequested)
                {
                    // Validate specific fleets provided by client
                    usedFleets = Imperial2030.Server.Helpers.ManeuverHelper.ValidateSpecificConvoyFleets(game, unit.TerritoryId, request.DestinationId, nation, request.ConvoyFleetIds!);
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
                    // Recorded for the log before the fleets are flagged below: once HasConvoyed is set
                    // the route can no longer be reconstructed from the board, and origin+destination
                    // alone never identified it (several sea routes can connect the same pair).
                    routeVia = Imperial2030.Server.Helpers.ManeuverHelper.BuildMoveRoute(game, unit.TerritoryId, request.DestinationId, nation, moveMode, usedFleets);

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

        if (request.IsHostile && !willFight
            && ManeuverHelper.IsProtectedLastFactoryProvince(game, nation, request.DestinationId, unit.Id))
        {
            return BadRequest("Cannot enter the last unoccupied factory of a nation hostilely. Must enter peacefully.");
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
                GameLogger.LogUnitMove(_context, game, unit.UnitType, sourceWasHostile, sourceTerritory, request.DestinationId, true, nation, controller.GetPlayerName(_context), routeVia);
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
                        GameLogger.LogUnitMove(_context, game, unit.UnitType, sourceWasHostile, sourceTerritory, request.DestinationId, true, nation, controller.GetPlayerName(_context), routeVia);
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
                    game.PendingBattleAggressorUnitId = unit.Id;
                    game.PendingBattleDefenders = foreignDefenders.ToList();

                    string peaceOrHostile = request.IsHostile ? "hostilely" : "peacefully";
                    GameLogger.LogUnitMoveAwaitingResponse(_context, game, UnitType.Army, sourceWasHostile, sourceTerritory, request.DestinationId, unit.IsHostile, string.Join(", ", foreignDefenders), nation, controller.GetPlayerName(_context), routeVia);
                }
            }
        }

        if (!game.PendingBattleDefenders.Any())
        {
            if (!moveAlreadyLogged)
            {
                GameLogger.LogUnitMove(_context, game, UnitType.Army, sourceWasHostile, sourceTerritory, request.DestinationId, unit.IsHostile, nation, controller.GetPlayerName(_context), routeVia);
            }
            // See MoveFleet: flag placement belongs to the end of the maneuver, not to each move.
            await TryAutoAdvanceManeuver(game, nation);
        }
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
                .ThenInclude(p => p.User)
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

        // Entering hostilely is refused by MoveArmy/MoveFleet for a nation's last working factory province
        // (Imperial-2030-Rules.pdf p.10); without this, that protection could simply be walked around by
        // entering peacefully and standing the army upright afterwards.
        if (!unit.IsHostile
            && ManeuverHelper.IsProtectedLastFactoryProvince(game, nation, unit.TerritoryId, unit.Id))
        {
            return BadRequest("Cannot blockade the last unoccupied factory of a nation.");
        }

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
                .ThenInclude(p => p.User)
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

        // 7. Minimum-factory exception. Imperial-2030-Rules.pdf p.10: "If a nation has only one factory
        // left that has not been occupied by hostile armies (standing upright), this factory cannot be
        // destroyed."
        //
        // The count is of UNOCCUPIED factories, not of all of them — a nation whose other factories are
        // already blockaded is down to this one. Destroying does not require occupying (the armies above
        // may be lying on their sides), so this and the entry protection are the same test: the province
        // is shielded exactly when it is not itself occupied and the owner has no other working factory.
        if (ManeuverHelper.IsProtectedLastFactoryProvince(game, nation, request.TerritoryId))
        {
            return BadRequest("Cannot destroy the last factory of a nation.");
        }

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
                     .ThenInclude(p => p.User)
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
            _logger.LogError(ex, "NextPhase failed for game {GameId}", gameId);
            return StatusCode(500, ErrorResponses.Internal(HttpContext?.TraceIdentifier));
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
                .ThenInclude(p => p.User)
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
                var aggUnit = aggressorUnits.FirstOrDefault(u => u.Id == game.PendingBattleAggressorUnitId)
                    ?? aggressorUnits.First();
                _context.Units.Remove(myUnit);
                _context.Units.Remove(aggUnit);
                game.Units.Remove(myUnit);
                game.Units.Remove(aggUnit);

                GameLogger.LogBattleResponseDestruction(_context, game, respondingNation, myUnit.UnitType, aggressorNation, aggUnit.UnitType, territoryId, respondingPlayerName);
                if (!SuppressBroadcasts) { await _hubContext.Clients.Group(gameId.ToString()).SendAsync("ShowToast", ToastBuilder.BuildBattleResponseToast(respondingNation, aggressorNation, isFight: true), false); }
            }

                game.PendingBattleTerritoryId = null;
                game.PendingBattleAggressorNation = null;
                game.PendingBattleAggressorUnitId = null;
                game.PendingBattleDefenders.Clear();

                // See MoveFleet: flags are settled at the end of the maneuver, not when this battle resolves.
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
                game.PendingBattleAggressorUnitId = null;
                game.PendingBattleDefenders.Clear();

                // See MoveFleet: flags are settled at the end of the maneuver, not when this battle resolves.
                // Advance Maneuver if applicable
                await TryAutoAdvanceManeuver(game, aggressorNation);
            }
        }
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

                        // Same limit TaxationRules.MaxFlagsPerNation caps tax revenue at - a
                        // nation only owns 15 flags, so it can never control more than 15 regions.
                        if (flagCount >= TaxationRules.MaxFlagsPerNation)
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
