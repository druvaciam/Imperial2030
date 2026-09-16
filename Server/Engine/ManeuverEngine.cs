using Imperial2030.Server.Data;
using Imperial2030.Server.Helpers;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Imperial2030.Server.Engine;

/// <summary>
/// Outcome of moving one unit. Exactly one of the three booleans describes what the move became: the
/// unit stayed where it was, it was destroyed in a 1:1 battle on arrival (with <see cref="DefeatedNation"/>'s
/// unit), or the arrival opened a battle negotiation that pauses the maneuver until the defenders answer.
/// None set means an uncontested move. <see cref="RouteVia"/> is what an army passed through (rail or
/// convoy), null for a plain step.
/// </summary>
public sealed record UnitMoveOutcome(
    bool Ok,
    string? Error = null,
    bool Stayed = false,
    bool MoverDestroyed = false,
    bool BattlePending = false,
    Nation? DefeatedNation = null,
    List<string>? RouteVia = null) : EngineResult(Ok, Error)
{
    public static new UnitMoveOutcome Fail(string error) => new(false, error);
}

/// <summary>
/// Outcome of a defender's answer to a pending battle. <see cref="BattleClosed"/> means no defender is
/// left to answer (or the aggressor is gone) and the aggressor's maneuver may resume.
/// </summary>
public sealed record BattleResponseOutcome(
    bool Ok,
    string? Error = null,
    Nation RespondingNation = default,
    Nation AggressorNation = default,
    bool Fought = false,
    bool BattleClosed = false,
    string ResponderName = "") : EngineResult(Ok, Error)
{
    public static new BattleResponseOutcome Fail(string error) => new(false, error);
}

/// <summary>
/// The Maneuver rondel action, Imperial-2030-Rules.pdf p.8-p.11: fleets move first, one sea region at a
/// time, a fleet leaving harbor always to the adjacent sea; armies then move one region overland, by rail
/// through the nation's own connected home provinces, or by convoy over the nation's fleets. An army
/// arriving hostile ("standing upright") in a region with foreign units may fight them 1:1; in a foreign
/// home province it may also attack a fleet still in harbor; with several foreign nations present, or
/// arriving peacefully, each defender is asked whether it fights. A nation's last factory province that is
/// not occupied by hostile armies may not be entered hostilely at all: "Armies of other nations that
/// enter this province are laid down on their sides" (p.10). Three armies on an undefended foreign
/// factory may destroy it, except a nation's last unoccupied one (p.10-11). Flags are placed at the end
/// of each phase for regions held exclusively, up to the nation's 15 (p.10).
///
/// Every method takes the game as loaded and mutates it in memory, logging through
/// <see cref="GameLogger"/>; none saves, broadcasts, or advances the phase on its own - the caller
/// decides when a phase ends (<see cref="TryAutoAdvanceManeuver"/> / <see cref="EndPhase"/>), because
/// the HTTP endpoint ends it after each move, the bot after walking every unit, and training per step.
/// </summary>
public static class ManeuverEngine
{
    // ------------------------------------------------------------------------------------------
    // Shared checks
    // ------------------------------------------------------------------------------------------

    private static string? ActiveNationRefusal(Game game, out Nation nation, out Player controller)
    {
        var current = game.CurrentTurnNation;
        nation = current;
        controller = null!;
        if (game.Status != GameStatus.InProgress) return "Game not in progress.";
        var nationState = game.NationStates.First(n => n.Nation == current);
        if (nationState.ControllerId == null) return "No controller for this nation.";
        var c = game.Players.FirstOrDefault(p => p.Id == nationState.ControllerId);
        if (c == null) return "No controller for this nation.";
        controller = c;
        return null;
    }

    /// <summary>The nations governed by the same player as <paramref name="nation"/>, itself included.</summary>
    public static HashSet<Nation> FriendlyNations(Game game, Player controller, Nation nation)
    {
        var friendly = game.NationStates.Where(n => n.ControllerId == controller.Id).Select(n => n.Nation).ToHashSet();
        friendly.Add(nation);
        return friendly;
    }

    /// <summary>
    /// Why a fleet could not sail from where it is to <paramref name="destinationId"/>, or null: not
    /// adjacent, a canal whose owner is not this player (Panama/Suez, p.11), or not a sea region (p.8: a
    /// fleet's destination is always the sea).
    /// </summary>
    public static string? FleetDestinationRefusal(Game game, Unit fleet, string destinationId, Player controller)
    {
        if (!MapConnectivity.Adjacency.TryGetValue(fleet.TerritoryId, out var neighbors)) return "Invalid current territory.";
        if (!neighbors.Contains(destinationId)) return "Destination is not adjacent.";

        // Canal Logic: Check if moving through Panama or Suez
        var canal = MapConnectivity.CanalLinks.FirstOrDefault(c =>
            (c.Region1 == fleet.TerritoryId && c.Region2 == destinationId) ||
            (c.Region1 == destinationId && c.Region2 == fleet.TerritoryId));
        if (canal != default)
        {
            var tState = game.TerritoryStates.FirstOrDefault(ts => ts.TerritoryId == canal.ControllerId);
            if (tState != null && tState.Controller != null && tState.Controller != fleet.Nation)
            {
                var canalNationState = game.NationStates.FirstOrDefault(ns => ns.Nation == tState.Controller.Value);
                var isSamePlayer = canalNationState != null && canalNationState.ControllerId == controller.Id;
                if (!isSamePlayer) return $"Passage through {canal.ControllerId} blocked by {tState.Controller}.";
            }
        }

        var destT = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == destinationId);
        if (destT == null) return "Invalid territory definition.";
        if (destT.Type != TerritoryType.Sea) return "Fleets can only move into sea regions.";
        return null;
    }

    /// <summary>
    /// p.10: a nation's last factory province not occupied by hostile armies may not be entered hostilely.
    /// True when <paramref name="territoryId"/> is such a province for a unit of <paramref name="nation"/>.
    /// </summary>
    public static bool MustEnterPeacefully(Game game, Nation nation, string territoryId, Guid? excludeUnitId = null)
        => ManeuverHelper.IsProtectedLastFactoryProvince(game, nation, territoryId, excludeUnitId);

    private static Unit? FindOwnUnit(Game game, Guid unitId, Nation nation, UnitType? type, out string? refusal)
    {
        refusal = null;
        var unit = game.Units.FirstOrDefault(u => u.Id == unitId);
        if (unit == null) { refusal = "Unit not found."; return null; }
        if (unit.Nation != nation) { refusal = "Not your unit."; return null; }
        if (type == UnitType.Fleet && unit.UnitType != UnitType.Fleet) { refusal = "Not a fleet."; return null; }
        if (type == UnitType.Army && unit.UnitType != UnitType.Army) { refusal = "Not an army."; return null; }
        return unit;
    }

    private static void RemoveUnit(ApplicationDbContext? context, Game game, Unit unit)
    {
        game.Units.Remove(unit);
        context?.Units.Remove(unit);
    }

    // ------------------------------------------------------------------------------------------
    // Staying put
    // ------------------------------------------------------------------------------------------

    /// <summary>The unit stays where it is for this maneuver ("any fleet may stay where it is", p.8).</summary>
    public static UnitMoveOutcome Stay(ApplicationDbContext? context, Game game, Guid unitId)
    {
        var refusal = ActiveNationRefusal(game, out var nation, out var controller);
        if (refusal != null) return UnitMoveOutcome.Fail(refusal);
        var unit = FindOwnUnit(game, unitId, nation, null, out refusal);
        if (unit == null) return UnitMoveOutcome.Fail(refusal!);
        if (unit.HasMoved) return UnitMoveOutcome.Fail("Unit already moved.");

        unit.HasMoved = true;
        if (context != null) context.Entry(unit).State = EntityState.Modified;
        GameLogger.LogUnitStay(context, game, unit.UnitType, unit.IsHostile, unit.TerritoryId, nation, controller.GetPlayerName(context));
        return new UnitMoveOutcome(true, Stayed: true);
    }

    // ------------------------------------------------------------------------------------------
    // Moving
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Moves a fleet one region. <paramref name="battleTargetNation"/> / <paramref name="battleTargetUnitType"/>
    /// name the exact unit to fight on arrival - set by replay from the already-logged battle; live play
    /// leaves them null and the engine decides from the rules.
    /// </summary>
    public static UnitMoveOutcome MoveFleet(ApplicationDbContext? context, Game game, Guid unitId, string destinationId, bool isHostile,
        Nation? battleTargetNation = null, UnitType? battleTargetUnitType = null)
    {
        var refusal = ActiveNationRefusal(game, out var nation, out var controller);
        if (refusal != null) return UnitMoveOutcome.Fail(refusal);
        if (game.CurrentManeuverPhase != ManeuverPhase.Fleets) return UnitMoveOutcome.Fail("Not in Fleet Maneuver phase.");
        if (game.PendingBattleDefenders.Any()) return UnitMoveOutcome.Fail("Cannot move fleets while a battle is pending.");

        var unit = FindOwnUnit(game, unitId, nation, UnitType.Fleet, out refusal);
        if (unit == null) return UnitMoveOutcome.Fail(refusal!);
        if (unit.HasMoved) return UnitMoveOutcome.Fail("Unit already moved.");
        if (unit.TerritoryId == destinationId) return Stay(context, game, unitId);

        refusal = FleetDestinationRefusal(game, unit, destinationId, controller);
        if (refusal != null) return UnitMoveOutcome.Fail(refusal);

        return Arrive(context, game, unit, destinationId, isHostile, battleTargetNation, battleTargetUnitType, routeVia: null, nation, controller);
    }

    /// <summary>
    /// Moves an army: one region overland, by rail, or by convoy - the mode is decided by
    /// <see cref="ManeuverHelper.DetermineArmyMoveMode"/> unless <paramref name="convoyFleetIds"/> names
    /// the carrying fleets, which is an explicit instruction to go by sea.
    /// </summary>
    public static UnitMoveOutcome MoveArmy(ApplicationDbContext? context, Game game, Guid unitId, string destinationId, bool isHostile,
        IReadOnlyList<Guid>? convoyFleetIds = null, Nation? battleTargetNation = null, UnitType? battleTargetUnitType = null)
    {
        var refusal = ActiveNationRefusal(game, out var nation, out var controller);
        if (refusal != null) return UnitMoveOutcome.Fail(refusal);
        if (game.CurrentManeuverPhase != ManeuverPhase.Armies) return UnitMoveOutcome.Fail("Not in Army Maneuver phase.");
        if (game.PendingBattleDefenders.Any()) return UnitMoveOutcome.Fail("Cannot move armies while a battle is pending.");

        var unit = FindOwnUnit(game, unitId, nation, UnitType.Army, out refusal);
        if (unit == null) return UnitMoveOutcome.Fail(refusal!);
        if (unit.HasMoved) return UnitMoveOutcome.Fail("Unit already moved.");
        if (unit.TerritoryId == destinationId) return Stay(context, game, unitId);

        if (!MapConnectivity.Adjacency.ContainsKey(unit.TerritoryId)) return UnitMoveOutcome.Fail("Invalid current territory.");
        var currentT = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == unit.TerritoryId);
        var destT = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == destinationId);
        if (currentT == null || destT == null) return UnitMoveOutcome.Fail("Invalid territory definition.");
        if (currentT.Type != TerritoryType.Land || destT.Type != TerritoryType.Land) return UnitMoveOutcome.Fail("Armies can only move on land.");

        // Naming fleets is an explicit instruction to go by sea: rail and convoy can reach the same pair
        // of territories, and a replay feeding a logged convoy back in must reproduce it as one.
        bool convoyRequested = convoyFleetIds != null && convoyFleetIds.Any();
        var moveMode = convoyRequested
            ? ManeuverHelper.ArmyMoveMode.Convoy
            : ManeuverHelper.DetermineArmyMoveMode(game, unit.TerritoryId, destinationId, nation);

        List<string>? routeVia = null;
        List<Unit>? usedFleets = null;
        if (moveMode == ManeuverHelper.ArmyMoveMode.Rail)
        {
            routeVia = ManeuverHelper.BuildMoveRoute(game, unit.TerritoryId, destinationId, nation, moveMode, null);
        }
        else if (moveMode == ManeuverHelper.ArmyMoveMode.Convoy)
        {
            usedFleets = convoyRequested
                ? ManeuverHelper.ValidateSpecificConvoyFleets(game, unit.TerritoryId, destinationId, nation, convoyFleetIds!.ToList())
                : ManeuverHelper.GetConvoyFleets(game, unit.TerritoryId, destinationId, nation);
            if (usedFleets == null)
            {
                return UnitMoveOutcome.Fail(convoyRequested
                    ? "Invalid convoy path with specified fleets."
                    : "Destination is not adjacent, and no valid rail or convoy path exists.");
            }
            // Recorded before the fleets are flagged: once HasConvoyed is set the route can no longer be
            // reconstructed from the board.
            routeVia = ManeuverHelper.BuildMoveRoute(game, unit.TerritoryId, destinationId, nation, moveMode, usedFleets);
        }

        // The protection check comes before anything is mutated, so a refusal leaves the fleets unflagged.
        var outcome = Arrive(context, game, unit, destinationId, isHostile, battleTargetNation, battleTargetUnitType, routeVia, nation, controller, dryRun: true);
        if (!outcome.Ok) return outcome;

        if (usedFleets != null)
        {
            foreach (var fleet in usedFleets)
            {
                fleet.HasConvoyed = true;
                if (context != null) context.Entry(fleet).State = EntityState.Modified;
            }
        }

        return Arrive(context, game, unit, destinationId, isHostile, battleTargetNation, battleTargetUnitType, routeVia, nation, controller);
    }

    /// <summary>
    /// The arrival shared by fleets and armies: the p.10 last-factory protection, hostility, and the
    /// battle the arrival does or does not cause. With <paramref name="dryRun"/> only the refusals are
    /// evaluated and nothing is mutated.
    /// </summary>
    private static UnitMoveOutcome Arrive(ApplicationDbContext? context, Game game, Unit unit, string destinationId, bool isHostile,
        Nation? battleTargetNation, UnitType? battleTargetUnitType, List<string>? routeVia, Nation nation, Player controller, bool dryRun = false)
    {
        // p.10: "may not be entered by hostile armies" - no exception for an entry that would fight.
        if (isHostile && MustEnterPeacefully(game, nation, destinationId, unit.Id))
        {
            return UnitMoveOutcome.Fail("Cannot enter the last unoccupied factory of a nation hostilely. Must enter peacefully.");
        }
        if (dryRun) return new UnitMoveOutcome(true);

        bool sourceWasHostile = unit.IsHostile;
        var sourceTerritory = unit.TerritoryId;
        var playerName = controller.GetPlayerName(context);
        var destDef = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == destinationId);
        bool isForeignHome = destDef != null && destDef.Nation.HasValue && destDef.Nation.Value != nation;
        bool isMyHome = destDef != null && destDef.Nation.HasValue && destDef.Nation.Value == nation;
        var friendlyNations = FriendlyNations(game, controller, nation);

        // Execute Move
        unit.TerritoryId = destinationId;
        unit.HasMoved = true;
        unit.IsHostile = isHostile;

        // Like fights like; entering a foreign home hostilely, the home nation's other unit type counts
        // too - that is what lets an army reach a fleet still in its harbor (p.10).
        bool IsDefender(Unit u) => u.UnitType == unit.UnitType || (isForeignHome && u.Nation == destDef!.Nation!.Value && isHostile);
        var foreignDefenders = game.Units
            .Where(u => u.TerritoryId == destinationId && !friendlyNations.Contains(u.Nation))
            .Where(IsDefender)
            .Select(u => u.Nation)
            .Distinct()
            .ToList();

        if (battleTargetNation.HasValue)
        {
            var targetNation = battleTargetNation.Value;
            var enemy = battleTargetUnitType.HasValue
                ? game.Units.FirstOrDefault(u => u.TerritoryId == destinationId && u.Nation == targetNation && u.UnitType == battleTargetUnitType.Value)
                : game.Units.FirstOrDefault(u => u.TerritoryId == destinationId && u.Nation == targetNation);
            if (enemy != null)
            {
                GameLogger.LogUnitMove(context, game, unit.UnitType, sourceWasHostile, sourceTerritory, destinationId, true, nation, playerName, routeVia);
                RemoveUnit(context, game, unit);
                RemoveUnit(context, game, enemy);
                GameLogger.LogBattleDestruction(context, game, unit.UnitType, targetNation, enemy.UnitType, destinationId, nation, playerName);
                return new UnitMoveOutcome(true, MoverDestroyed: true, DefeatedNation: targetNation, RouteVia: routeVia);
            }
        }
        else if (foreignDefenders.Any())
        {
            // Foreign units in your own home province are always met hostilely; there is no coexisting.
            if (isMyHome)
            {
                isHostile = true;
                unit.IsHostile = true;
            }

            if (isHostile && foreignDefenders.Count == 1)
            {
                // One defending nation: the battle resolves 1:1 on the spot.
                var targetNation = foreignDefenders[0];
                var enemy = game.Units.FirstOrDefault(u => u.TerritoryId == destinationId && u.Nation == targetNation && IsDefender(u));
                if (enemy != null)
                {
                    GameLogger.LogUnitMove(context, game, unit.UnitType, sourceWasHostile, sourceTerritory, destinationId, true, nation, playerName, routeVia);
                    RemoveUnit(context, game, unit);
                    RemoveUnit(context, game, enemy);
                    GameLogger.LogBattleDestruction(context, game, unit.UnitType, targetNation, enemy.UnitType, destinationId, nation, playerName);
                    return new UnitMoveOutcome(true, MoverDestroyed: true, DefeatedNation: targetNation, RouteVia: routeVia);
                }
            }
            else
            {
                // Peaceful arrival, or several defending nations: each defender chooses whether to fight.
                game.PendingBattleTerritoryId = destinationId;
                game.PendingBattleAggressorNation = nation;
                game.PendingBattleAggressorUnitId = unit.Id;
                game.PendingBattleDefenders = foreignDefenders.ToList();
                GameLogger.LogUnitMoveAwaitingResponse(context, game, unit.UnitType, sourceWasHostile, sourceTerritory, destinationId, unit.IsHostile, string.Join(", ", foreignDefenders), nation, playerName, routeVia);
                return new UnitMoveOutcome(true, BattlePending: true, RouteVia: routeVia);
            }
        }

        GameLogger.LogUnitMove(context, game, unit.UnitType, sourceWasHostile, sourceTerritory, destinationId, unit.IsHostile, nation, playerName, routeVia);
        return new UnitMoveOutcome(true, RouteVia: routeVia);
    }

    // ------------------------------------------------------------------------------------------
    // Hostility and battles without moving
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Stands a unit upright or lays it down where it is. Standing up in a nation's last unoccupied
    /// factory province is refused: it would be the p.10 entry rule walked around in two steps.
    /// </summary>
    public static EngineResult ToggleHostility(ApplicationDbContext? context, Game game, Guid unitId)
    {
        var refusal = ActiveNationRefusal(game, out var nation, out var controller);
        if (refusal != null) return EngineResult.Fail(refusal);
        if (game.CurrentManeuverPhase == ManeuverPhase.None) return EngineResult.Fail("Not in Maneuver phase.");

        var unit = game.Units.FirstOrDefault(u => u.Id == unitId);
        if (unit == null) return EngineResult.Fail("Unit not found.");
        if (unit.Nation != nation) return EngineResult.Fail("You can only toggle hostility of your own units.");

        if (!unit.IsHostile && MustEnterPeacefully(game, nation, unit.TerritoryId, unit.Id))
        {
            return EngineResult.Fail("Cannot blockade the last unoccupied factory of a nation.");
        }

        unit.IsHostile = !unit.IsHostile;
        GameLogger.LogHostilityToggle(context, game, unit.UnitType, unit.TerritoryId, unit.IsHostile, nation, controller.GetPlayerName(context));
        return EngineResult.Success;
    }

    /// <summary>
    /// A unit that has just stood up where foreign units already are is the same act of aggression as
    /// walking in hostilely, and p.10 lets the defenders answer it the same way: one defending nation
    /// fights 1:1 on the spot, several open the negotiation. Returns what the move path would.
    /// </summary>
    public static UnitMoveOutcome ResolveStationaryBattle(ApplicationDbContext? context, Game game, Guid unitId)
    {
        var refusal = ActiveNationRefusal(game, out var nation, out var controller);
        if (refusal != null) return UnitMoveOutcome.Fail(refusal);
        var unit = FindOwnUnit(game, unitId, nation, null, out refusal);
        if (unit == null) return UnitMoveOutcome.Fail(refusal!);

        var territoryId = unit.TerritoryId;
        var friendlyNations = FriendlyNations(game, controller, nation);
        var def = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == territoryId);
        bool isForeignHome = def != null && def.Nation.HasValue && !friendlyNations.Contains(def.Nation.Value);
        bool IsReachableDefender(Unit u) => u.UnitType == unit.UnitType || (isForeignHome && u.Nation == def!.Nation!.Value);

        var foreignDefenders = game.Units
            .Where(u => u.TerritoryId == territoryId && u.Id != unit.Id && !friendlyNations.Contains(u.Nation))
            .Where(IsReachableDefender)
            .Select(u => u.Nation)
            .Distinct()
            .ToList();

        if (foreignDefenders.Count == 0) return new UnitMoveOutcome(true);

        if (foreignDefenders.Count == 1)
        {
            var targetNation = foreignDefenders[0];
            var enemy = game.Units.FirstOrDefault(u => u.TerritoryId == territoryId && u.Nation == targetNation && IsReachableDefender(u));
            if (enemy == null) return new UnitMoveOutcome(true);

            RemoveUnit(context, game, unit);
            RemoveUnit(context, game, enemy);
            GameLogger.LogBattleDestruction(context, game, unit.UnitType, targetNation, enemy.UnitType, territoryId, nation, controller.GetPlayerName(context));
            return new UnitMoveOutcome(true, MoverDestroyed: true, DefeatedNation: targetNation);
        }

        game.PendingBattleTerritoryId = territoryId;
        game.PendingBattleAggressorNation = nation;
        game.PendingBattleAggressorUnitId = unit.Id;
        game.PendingBattleDefenders = foreignDefenders.ToList();
        return new UnitMoveOutcome(true, BattlePending: true);
    }

    /// <summary>
    /// A unit attacks a named nation's unit in the region it already stands in (p.10: "Armies of foreign
    /// nations can call for a battle if their land region has been invaded"; fleets likewise at sea or in
    /// harbor). Both are removed.
    /// </summary>
    public static EngineResult StationaryBattle(ApplicationDbContext? context, Game game, Guid unitId, Nation targetNation)
    {
        var refusal = ActiveNationRefusal(game, out var nation, out var controller);
        if (refusal != null) return EngineResult.Fail(refusal);
        if (game.PendingBattleDefenders.Any()) return EngineResult.Fail("Cannot initiate battles while another battle is pending.");

        var unit = FindOwnUnit(game, unitId, nation, null, out refusal);
        if (unit == null) return EngineResult.Fail(refusal!);
        if (unit.HasMoved) return EngineResult.Fail("Unit already moved this turn.");

        var enemyUnit = game.Units.FirstOrDefault(u => u.TerritoryId == unit.TerritoryId && u.Nation == targetNation);
        if (enemyUnit == null) return EngineResult.Fail($"No {targetNation} {unit.UnitType} in {unit.TerritoryId}.");

        RemoveUnit(context, game, unit);
        RemoveUnit(context, game, enemyUnit);
        GameLogger.LogBattleDestruction(context, game, unit.UnitType, targetNation, enemyUnit.UnitType, unit.TerritoryId, nation, controller.GetPlayerName(context));
        return EngineResult.Success;
    }

    // ------------------------------------------------------------------------------------------
    // Factory destruction (p.10-11)
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Foreign factory provinces where <paramref name="nation"/> has at least
    /// <see cref="ManeuverRules.DestroyFactoryArmyCost"/> armies, no defender is present, and the factory
    /// is not the owner's protected last one.
    /// </summary>
    public static List<string> FactoryDestructionCandidates(Game game, Nation nation, Player controller)
    {
        var friendlyNations = FriendlyNations(game, controller, nation);
        var candidates = new List<string>();

        foreach (var group in game.Units.Where(u => u.Nation == nation && u.UnitType == UnitType.Army).GroupBy(u => u.TerritoryId))
        {
            if (group.Count() < ManeuverRules.DestroyFactoryArmyCost) continue;
            if (DestroyFactoryRefusal(game, nation, group.Key, friendlyNations) == null) candidates.Add(group.Key);
        }
        return candidates;
    }

    private static string? DestroyFactoryRefusal(Game game, Nation nation, string territoryId, HashSet<Nation> friendlyNations)
    {
        var territoryDef = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == territoryId);
        if (territoryDef == null) return "Invalid territory.";
        var tState = game.TerritoryStates.FirstOrDefault(ts => ts.TerritoryId == territoryId);
        if (tState == null || !tState.HasFactory) return "No factory here.";
        if (!territoryDef.Nation.HasValue) return "Not a home province.";
        var defenderNation = territoryDef.Nation.Value;
        if (friendlyNations.Contains(defenderNation)) return "Cannot destroy your own factory.";
        if (game.Units.Any(u => u.TerritoryId == territoryId && u.Nation == defenderNation)) return "Cannot destroy factory while defenders are present.";
        // p.10-11: "If a nation has only one factory left that has not been occupied by hostile armies
        // (standing upright), this factory cannot be destroyed."
        if (ManeuverHelper.IsProtectedLastFactoryProvince(game, nation, territoryId)) return "Cannot destroy the last factory of a nation.";
        return null;
    }

    /// <summary>
    /// Destroys the foreign factory in <paramref name="territoryId"/> with three of the nation's armies
    /// there, which are removed with it. <paramref name="unitIds"/> names them; null takes any three.
    /// </summary>
    public static EngineResult DestroyFactory(ApplicationDbContext? context, Game game, string territoryId, IReadOnlyList<Guid>? unitIds = null)
    {
        var refusal = ActiveNationRefusal(game, out var nation, out var controller);
        if (refusal != null) return EngineResult.Fail(refusal);
        if (game.PendingBattleDefenders.Any()) return EngineResult.Fail("Cannot destroy factories while a battle is pending.");

        refusal = DestroyFactoryRefusal(game, nation, territoryId, FriendlyNations(game, controller, nation));
        if (refusal != null) return EngineResult.Fail(refusal);

        List<Unit> attackingUnits;
        if (unitIds != null)
        {
            if (unitIds.Count != ManeuverRules.DestroyFactoryArmyCost) return EngineResult.Fail($"Must provide exactly {ManeuverRules.DestroyFactoryArmyCost} armies.");
            attackingUnits = new List<Unit>();
            foreach (var uid in unitIds)
            {
                var u = game.Units.FirstOrDefault(x => x.Id == uid);
                if (u == null) return EngineResult.Fail($"Unit {uid} not found.");
                if (u.Nation != nation) return EngineResult.Fail("Not your unit.");
                if (u.UnitType != UnitType.Army) return EngineResult.Fail("Must use armies.");
                if (u.TerritoryId != territoryId) return EngineResult.Fail("Army not in territory.");
                attackingUnits.Add(u);
            }
        }
        else
        {
            attackingUnits = game.Units
                .Where(u => u.Nation == nation && u.UnitType == UnitType.Army && u.TerritoryId == territoryId)
                .Take(ManeuverRules.DestroyFactoryArmyCost)
                .ToList();
            if (attackingUnits.Count < ManeuverRules.DestroyFactoryArmyCost) return EngineResult.Fail($"Must provide exactly {ManeuverRules.DestroyFactoryArmyCost} armies.");
        }

        foreach (var u in attackingUnits) RemoveUnit(context, game, u);
        var tState = game.TerritoryStates.First(ts => ts.TerritoryId == territoryId);
        tState.HasFactory = false;
        if (context != null) context.Entry(tState).State = EntityState.Modified;

        GameLogger.LogFactoryDestruction(context, game, territoryId, nation, controller.GetPlayerName(context));
        return EngineResult.Success;
    }

    // ------------------------------------------------------------------------------------------
    // Battle negotiation
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// A defending nation answers the pending battle about the unit that just arrived (p.8: the others
    /// "are asked one after another if they want to do battle"). Fighting destroys that unit and one of
    /// the defender's, which ends the negotiation - there is nothing left for the others to answer about.
    /// Declining passes the question to the next defender; when the last has declined, everyone stays.
    /// Resuming the aggressor's maneuver is the caller's step.
    /// </summary>
    public static BattleResponseOutcome RespondToBattle(ApplicationDbContext? context, Game game, Nation respondingNation, bool fight)
    {
        if (game.PendingBattleTerritoryId == null || game.PendingBattleAggressorNation == null || !game.PendingBattleDefenders.Any())
            return BattleResponseOutcome.Fail("No pending battle.");
        if (!game.PendingBattleDefenders.Contains(respondingNation))
            return BattleResponseOutcome.Fail($"{respondingNation} is not a defender in this battle.");

        var territoryId = game.PendingBattleTerritoryId;
        var aggressorNation = game.PendingBattleAggressorNation.Value;
        var respondingController = game.Players.FirstOrDefault(p => p.Id == game.NationStates.First(ns => ns.Nation == respondingNation).ControllerId);
        string responderName = respondingController?.GetPlayerName(context) ?? GameConstants.SystemPlayerName;

        var defenders = game.PendingBattleDefenders.ToList();
        defenders.Remove(respondingNation);
        game.PendingBattleDefenders = defenders;
        if (context != null) context.Entry(game).Property(g => g.PendingBattleDefenders).IsModified = true;

        if (fight)
        {
            var myUnit = game.Units.FirstOrDefault(u => u.TerritoryId == territoryId && u.Nation == respondingNation);
            var aggUnit = game.Units.FirstOrDefault(u => u.TerritoryId == territoryId && u.Nation == aggressorNation && u.Id == game.PendingBattleAggressorUnitId)
                ?? game.Units.FirstOrDefault(u => u.TerritoryId == territoryId && u.Nation == aggressorNation);
            if (myUnit != null && aggUnit != null)
            {
                RemoveUnit(context, game, myUnit);
                RemoveUnit(context, game, aggUnit);
                GameLogger.LogBattleResponseDestruction(context, game, respondingNation, myUnit.UnitType, aggressorNation, aggUnit.UnitType, territoryId, responderName);
            }
        }
        else
        {
            GameLogger.LogBattleResponsePeace(context, game, respondingNation, aggressorNation, territoryId, responderName);
        }

        bool aggressorGone = !game.Units.Any(u => u.TerritoryId == territoryId && u.Nation == aggressorNation);
        bool closed = fight || !game.PendingBattleDefenders.Any() || aggressorGone;
        if (closed)
        {
            if (!fight) GameLogger.LogAllPartiesPeace(context, game, territoryId, GameConstants.SystemPlayerName);
            game.PendingBattleTerritoryId = null;
            game.PendingBattleAggressorNation = null;
            game.PendingBattleAggressorUnitId = null;
            game.PendingBattleDefenders.Clear();
        }

        return new BattleResponseOutcome(true, RespondingNation: respondingNation, AggressorNation: aggressorNation, Fought: fight, BattleClosed: closed, ResponderName: responderName);
    }

    // ------------------------------------------------------------------------------------------
    // Phases and flags
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// The active player ends the current phase by hand: flags are placed for it (p.10, step 3) and the
    /// phase moves from Fleets to Armies, or from Armies to done.
    /// </summary>
    public static EngineResult EndPhase(ApplicationDbContext? context, Game game)
    {
        var refusal = ActiveNationRefusal(game, out var nation, out var controller);
        if (refusal != null) return EngineResult.Fail(refusal);
        if (game.PendingBattleDefenders.Any()) return EngineResult.Fail("Cannot advance phase while a battle is pending.");

        var oldPhase = game.CurrentManeuverPhase;
        switch (oldPhase)
        {
            case ManeuverPhase.Fleets:
                UpdateTerritoryControl(context, game);
                game.CurrentManeuverPhase = ManeuverPhase.Armies;
                break;
            case ManeuverPhase.Armies:
                UpdateTerritoryControl(context, game);
                game.CurrentManeuverPhase = ManeuverPhase.None;
                break;
            default:
                return EngineResult.Fail("Invalid phase transition.");
        }

        GameLogger.LogEndManeuverPhase(context, game, oldPhase.ToString(), nation, controller.GetPlayerName(context));
        return EngineResult.Success;
    }

    /// <summary>
    /// Ends the Fleets phase when the nation has no unmoved fleet, then the Armies phase when it has no
    /// unmoved army - placing flags at each phase end (p.10, step 3) and logging the automatic end.
    /// </summary>
    public static void TryAutoAdvanceManeuver(ApplicationDbContext? context, Game game, Nation nation)
    {
        var playerName = NationControllerName(game, nation, context);

        if (game.CurrentManeuverPhase == ManeuverPhase.Fleets
            && !game.Units.Any(u => u.Nation == nation && u.UnitType == UnitType.Fleet && !u.HasMoved))
        {
            UpdateTerritoryControl(context, game);
            game.CurrentManeuverPhase = ManeuverPhase.Armies;
            GameLogger.LogAutoEndManeuverPhase(context, game, "Fleets", nation, playerName);
        }

        if (game.CurrentManeuverPhase == ManeuverPhase.Armies
            && !game.Units.Any(u => u.Nation == nation && u.UnitType == UnitType.Army && !u.HasMoved))
        {
            UpdateTerritoryControl(context, game);
            game.CurrentManeuverPhase = ManeuverPhase.None;
            GameLogger.LogAutoEndManeuverPhase(context, game, "Armies", nation, playerName);
        }
    }

    /// <summary>The player governing <paramref name="nation"/>, for the log; "System" if none.</summary>
    public static string NationControllerName(Game game, Nation nation, ApplicationDbContext? context)
    {
        var controllerId = game.NationStates.FirstOrDefault(ns => ns.Nation == nation)?.ControllerId;
        var controller = controllerId.HasValue ? game.Players.FirstOrDefault(p => p.Id == controllerId.Value) : null;
        return controller?.GetPlayerName(context) ?? GameConstants.SystemPlayerName;
    }

    /// <summary>
    /// Flag placement, p.10 step 3: every region held exclusively by one nation's units gets that nation's
    /// flag, replacing another nation's; home provinces take no flags; a nation with all 15 flags out
    /// removes the previous flag without replacing it.
    /// </summary>
    public static void UpdateTerritoryControl(ApplicationDbContext? context, Game game)
    {
        var territoriesWithUnits = game.Units.Select(u => u.TerritoryId).Distinct().ToList();

        foreach (var tId in territoriesWithUnits)
        {
            var unitsInTerritory = game.Units.Where(u => u.TerritoryId == tId).ToList();
            if (!unitsInTerritory.Any()) continue;

            var firstNation = unitsInTerritory.First().Nation;
            if (!unitsInTerritory.All(u => u.Nation == firstNation)) continue;

            var territoryDef = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == tId);
            if (territoryDef == null) continue;

            var states = game.TerritoryStates.Where(ts => ts.TerritoryId == tId).ToList();
            var tState = states.FirstOrDefault();
            // Clean up duplicates caused by concurrent API calls
            for (int i = 1; i < states.Count; i++)
            {
                game.TerritoryStates.Remove(states[i]);
                context?.TerritoryStates.Remove(states[i]);
            }
            if (tState == null)
            {
                tState = new TerritoryState { TerritoryId = tId, GameId = game.Id };
                game.TerritoryStates.Add(tState);
                context?.TerritoryStates.Add(tState);
            }

            // Flags are NOT placed on home provinces.
            bool isHomeProvince = territoryDef.Nation.HasValue;
            if (isHomeProvince || tState.Controller == firstNation) continue;

            var oldController = tState.Controller;
            int flagCount = game.TerritoryStates.Count(ts => ts.Controller == firstNation);
            var playerName = NationControllerName(game, firstNation, context);

            if (flagCount >= TaxationRules.MaxFlagsPerNation)
            {
                if (oldController != null)
                {
                    tState.Controller = null;
                    GameLogger.LogTerritoryControlChange(context, game, territoryDef.Name, oldController, null, playerName);
                }
            }
            else
            {
                tState.Controller = firstNation;
                GameLogger.LogTerritoryControlChange(context, game, territoryDef.Name, oldController, firstNation, playerName);
            }
        }
    }
}
