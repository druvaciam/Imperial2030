using System.Text.Json;
using Imperial2030.Server.Controllers;
using Imperial2030.Server.Data;
using Imperial2030.Server.Helpers;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Imperial2030.Server.Services;

public class GameReplayResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public long? FailedActionOrderIndex { get; set; }
    public string? FailedActionType { get; set; }
}

/// <summary>
/// Reconstructs a game's state purely from its logged <see cref="GameAction"/> history by replaying each
/// action through the real <see cref="GamesController"/>/<see cref="ManeuverController"/> endpoints.
///
/// Extracted from Tests/ReplayGameTests.cs's TestReplayabilityFromActions, where this exact logic was
/// hardened against a real intermittent replay-divergence bug this session (EF change-tracker staleness,
/// pending-battle leftovers, rondel-position drift, etc.) — see the inline diagnostics below, several of
/// which are still load-bearing for catching the same class of bug if it resurfaces in production.
/// </summary>
public class GameReplayService
{
    private readonly ILogger<GameReplayService> _logger;

    public GameReplayService(ILogger<GameReplayService>? logger = null)
    {
        _logger = logger ?? NullLogger<GameReplayService>.Instance;
    }

    private static readonly JsonSerializerOptions MetaJsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Builds the "a MoveArmy/MoveFleet action had no unit to move" failure diagnostic. That symptom is
    /// never the root cause — it's the first point at which an EARLIER divergence becomes fatal — so this
    /// deliberately reports (a) the whole replayed board, (b) a from-the-log reconstruction of how many
    /// units of this nation/type there *should* be at this point, and (c) every logged action that could
    /// have created or destroyed one of them.
    ///
    /// The nation filter can't just be `a.Nation == action.Nation`: a "Battle" is logged under the
    /// AGGRESSOR's nation when it auto-resolves (GameLogger.LogBattleDestruction) but under the
    /// RESPONDING/defending nation when it comes from a battle response (LogBattleResponseDestruction), so
    /// half of a nation's unit losses would be invisible without also matching on the metadata's
    /// Aggressor/Defender nations. Production/Import (the only two ways units are created) matter for the
    /// same reason and were missing from the original, narrower filter.
    /// </summary>
    private string BuildMissingUnitDiagnostic(
        ApplicationDbContext context, Guid replayGameId,
        IReadOnlyList<GameActionDto> actions, GameActionDto action, UnitType unitType,
        List<UnitLedgerEntry> unitLedger)
    {
        var nation = action.Nation;
        var unitsOfNation = context.Units.Where(u => u.GameId == replayGameId && u.Nation == nation).ToList();
        var allUnits = context.Units.Where(u => u.GameId == replayGameId).ToList();

        // Reconstruct the expected count purely from the log, so the report distinguishes "replay never
        // created these units" from "replay destroyed units the original kept".
        int produced = 0, imported = 0, lostInBattle = 0, sacrificedToFactoryDestruction = 0;
        var relevant = new List<string>();
        foreach (var a in actions.Where(a => a.OrderIndex <= action.OrderIndex).OrderBy(a => a.OrderIndex))
        {
            bool involvesNation = a.Nation == nation;
            switch (a.ActionType)
            {
                case "Production":
                    if (!involvesNation) break;
                    var pm = TryDeserialize<ProductionMetadata>(a.Metadata);
                    produced += pm?.Units?.Count(u => u.UnitType == unitType) ?? 0;
                    relevant.Add($"#{a.OrderIndex} Production Meta={a.Metadata}");
                    break;
                case "Import":
                    if (!involvesNation) break;
                    var im = TryDeserialize<ImportMetadata>(a.Metadata);
                    imported += im?.Units?.Count(u => u.UnitType == unitType) ?? 0;
                    relevant.Add($"#{a.OrderIndex} Import Meta={a.Metadata}");
                    break;
                case "Battle":
                    var bm = TryDeserialize<ActionMetadata>(a.Metadata);
                    bool aggIsNation = bm?.AggressorNation == nation;
                    bool defIsNation = bm?.DefenderNation == nation;
                    if (!aggIsNation && !defIsNation && !involvesNation) break;
                    if (aggIsNation && bm?.UnitType == unitType) lostInBattle++;
                    if (defIsNation && bm?.DefenderUnitType == unitType) lostInBattle++;
                    relevant.Add($"#{a.OrderIndex} Battle Meta={a.Metadata}");
                    break;
                case "DestroyFactory":
                    if (!involvesNation) break;
                    if (unitType == UnitType.Army) sacrificedToFactoryDestruction += ManeuverRules.DestroyFactoryArmyCost;
                    relevant.Add($"#{a.OrderIndex} DestroyFactory Meta={a.Metadata}");
                    break;
                case "MoveArmy":
                case "MoveFleet":
                case "BattleResponse":
                case "ToggleHostility":
                    if (!involvesNation) break;
                    relevant.Add($"#{a.OrderIndex} {a.ActionType} Meta={a.Metadata}");
                    break;
            }
        }

        var boardSummary = allUnits
            .GroupBy(u => (u.Nation, u.UnitType))
            .OrderBy(g => g.Key.Nation).ThenBy(g => g.Key.UnitType)
            .Select(g => $"{g.Key.Nation} {g.Key.UnitType}={g.Count()}");

        return
            $"All current {nation} units ({unitsOfNation.Count}): " +
            $"{string.Join(", ", unitsOfNation.Select(u => $"[{u.UnitType} {u.TerritoryId} HasMoved={u.HasMoved} Hostile={u.IsHostile}]"))}. " +
            $"Whole replayed board: {string.Join(", ", boardSummary)}. " +
            $"Log-derived {nation} {unitType} accounting up to #{action.OrderIndex}: produced={produced}, imported={imported}, " +
            $"lostInBattle={lostInBattle}, sacrificedToFactoryDestruction={sacrificedToFactoryDestruction} " +
            $"(actual on replay board={unitsOfNation.Count(u => u.UnitType == unitType)}). " +
            $"Unit-affecting history for {nation}:\n{string.Join("\n", relevant)}\n" +
            $"What the REPLAY actually did to {nation} {unitType} units:\n" +
            $"{string.Join("\n", unitLedger.Where(e => e.Nation == nation && e.UnitType == unitType).Select(e => e.Line))}";
    }

    private record UnitLedgerEntry(Nation Nation, UnitType UnitType, string Line);

    /// <summary>
    /// Turns the route recorded on a move's log entry (ActionMetadata.RouteVia) into the fleet ids on the
    /// replay board that sit in those sea regions, so the move can be replayed along the SAME route it
    /// originally took.
    ///
    /// Without this the replay calls MoveArmy with no fleets named and the endpoint auto-selects its own
    /// convoy - a legal route, but frequently not the one the original game used, which then gets written
    /// into the replayed game's log as a different journey. That is what made a replay draw an army
    /// crossing seas it never went near.
    ///
    /// Returns null when the route names no sea regions (a rail-only move, which needs no fleets) or when
    /// a region on it has no free fleet on the replay board. Null means "no instruction", leaving the
    /// endpoint to choose as before - the same behaviour as an action logged before RouteVia existed.
    /// </summary>
    private static List<Guid>? ResolveLoggedConvoyFleets(
        ApplicationDbContext context, Guid replayGameId, Nation nation, List<string>? routeVia)
    {
        if (routeVia == null || routeVia.Count == 0) return null;

        var seaLegs = routeVia
            .Where(id => TerritoryData.AllTerritories.Any(t => t.Id == id && t.Type == TerritoryType.Sea))
            .ToList();
        if (seaLegs.Count == 0) return null;

        var fleetIds = new List<Guid>();
        foreach (var seaId in seaLegs)
        {
            var fleet = context.Units.FirstOrDefault(u =>
                u.GameId == replayGameId && u.Nation == nation && u.UnitType == UnitType.Fleet
                && u.TerritoryId == seaId && !u.HasConvoyed);
            if (fleet == null) return null;
            fleetIds.Add(fleet.Id);
        }
        return fleetIds;
    }

    private static Dictionary<Guid, (Nation Nation, UnitType UnitType, string TerritoryId)> SnapshotUnits(
        ApplicationDbContext context, Guid replayGameId)
    {
        return context.Units.Where(u => u.GameId == replayGameId)
            .Select(u => new { u.Id, u.Nation, u.UnitType, u.TerritoryId })
            .ToDictionary(u => u.Id, u => (u.Nation, u.UnitType, u.TerritoryId));
    }

    /// <summary>
    /// Diffs the replay board against the previous snapshot and attributes every created/destroyed unit to
    /// <paramref name="previousAction"/> (the action replayed since that snapshot was taken).
    /// </summary>
    private static Dictionary<Guid, (Nation Nation, UnitType UnitType, string TerritoryId)> RecordUnitChanges(
        ApplicationDbContext context, Guid replayGameId,
        Dictionary<Guid, (Nation Nation, UnitType UnitType, string TerritoryId)> previousSnapshot,
        GameActionDto? previousAction,
        List<UnitLedgerEntry> ledger)
    {
        var current = SnapshotUnits(context, replayGameId);
        if (previousAction == null) return current;

        string by = $"#{previousAction.OrderIndex} {previousAction.ActionType}({previousAction.Nation})";
        foreach (var (id, u) in previousSnapshot)
        {
            if (!current.ContainsKey(id))
            {
                ledger.Add(new UnitLedgerEntry(u.Nation, u.UnitType, $"  {by} DESTROYED {u.Nation} {u.UnitType} @ {u.TerritoryId}"));
            }
        }
        foreach (var (id, u) in current)
        {
            if (!previousSnapshot.ContainsKey(id))
            {
                ledger.Add(new UnitLedgerEntry(u.Nation, u.UnitType, $"  {by} CREATED {u.Nation} {u.UnitType} @ {u.TerritoryId}"));
            }
        }
        return current;
    }

    /// <summary>
    /// Records every unit that disappeared while the live MoveArmy/MoveFleet endpoint ran, attributing each
    /// to the move's DESTINATION territory — the only place that endpoint's auto-combat destroys anything
    /// (the mover after it arrives, plus the single defender it engages). Attributing by destination rather
    /// than by the unit's last known position matters for the aggressor: the general per-action ledger sees
    /// it vanish from its ORIGIN territory, which would never match the following "Battle" action's
    /// TerritoryId.
    /// </summary>
    private static void RecordMoveCombatDestructions(
        ApplicationDbContext context, Guid replayGameId,
        Dictionary<Guid, (Nation Nation, UnitType UnitType, string TerritoryId)> beforeMove,
        string destinationTerritoryId,
        List<(Nation Nation, UnitType UnitType, string TerritoryId)> destroyed)
    {
        var afterMove = SnapshotUnits(context, replayGameId);
        foreach (var (id, u) in beforeMove)
        {
            if (!afterMove.ContainsKey(id))
            {
                destroyed.Add((u.Nation, u.UnitType, destinationTerritoryId));
            }
        }
    }

    /// <summary>
    /// Finds and removes (consumes) a record of a unit the previous replayed action destroyed, matching the
    /// given territory/nation and — when the logged battle metadata specifies one — unit type. Returns true
    /// when a match was consumed, meaning that side of the battle is already accounted for.
    /// </summary>
    private static bool ConsumeDestroyedUnit(
        List<(Nation Nation, UnitType UnitType, string TerritoryId)> destroyed,
        string territoryId, Nation nation, UnitType? unitType)
    {
        int idx = destroyed.FindIndex(d => d.TerritoryId == territoryId && d.Nation == nation && (!unitType.HasValue || d.UnitType == unitType.Value));
        if (idx < 0) return false;
        destroyed.RemoveAt(idx);
        return true;
    }

    private static T? TryDeserialize<T>(string? metadata) where T : class
    {
        if (string.IsNullOrEmpty(metadata)) return null;
        try { return JsonSerializer.Deserialize<T>(metadata, MetaJsonOptions); }
        catch { return null; }
    }

    /// <summary>
    /// Looks one action ahead in the log for the "Battle" entry that a MoveArmy/MoveFleet at
    /// <paramref name="currentIndex"/> would auto-resolve into (same destination territory, this mover's
    /// nation as aggressor). When found, its DefenderNation/DefenderUnitType are exactly what the original
    /// game recorded as destroyed — feeding them back into the replayed move via
    /// BattleTargetNation/BattleTargetUnitType makes the live endpoint target that same specific unit
    /// instead of letting its own (deliberately unconstrained — the rules give this choice to the attacking
    /// player, not yet exposed as a UI/bot decision) auto-resolve pick arbitrarily among several candidates.
    /// This only ever affects replay: live play never has this lookahead available or needed.
    /// </summary>
    private static (Nation DefenderNation, UnitType? DefenderUnitType)? FindAutoResolvedBattleTarget(
        IReadOnlyList<GameActionDto> actions, int currentIndex, string destinationTerritoryId, Nation moverNation)
    {
        if (currentIndex + 1 >= actions.Count) return null;
        var next = actions[currentIndex + 1];
        if (next.ActionType != "Battle") return null;
        var bm = TryDeserialize<ActionMetadata>(next.Metadata);
        if (bm == null || bm.TerritoryId != destinationTerritoryId || bm.AggressorNation != moverNation || !bm.DefenderNation.HasValue) return null;
        return (bm.DefenderNation.Value, bm.DefenderUnitType);
    }

    public async Task<GameReplayResult> ReplayActionsAsync(
        ApplicationDbContext context, Guid gameId,
        GamesController gamesController, ManeuverController maneuverController,
        IReadOnlyList<GameActionDto> actions, bool suppressBroadcasts = false,
        // Invoked once per action with (action, index, wasSkipped). wasSkipped is true for the informational
        // entries the skip-list below treats as no-ops — they advance the index but change no state, so a
        // paced viewer (ReplaySessionManager) can advance past them instantly instead of spending a full
        // beat showing nothing.
        Func<GameActionDto, int, bool, Task>? onActionReplayed = null)
    {
        var replayGameId = gameId;

        var previousGamesControllerSuppress = gamesController.SuppressBroadcasts;
        var previousManeuverControllerSuppress = maneuverController.SuppressBroadcasts;
        if (suppressBroadcasts)
        {
            gamesController.SuppressBroadcasts = true;
            maneuverController.SuppressBroadcasts = true;
        }

        // Ledger of every unit this replay creates or destroys, attributed to the action that did it.
        // "A MoveArmy/MoveFleet has no unit to move" is always a *downstream* symptom of some earlier
        // action having destroyed (or failed to create) a unit, and the replayed action responsible is
        // otherwise invisible — a logged "Battle" and the live MoveArmy endpoint's own auto-combat both
        // remove units, as does DestroyFactory, so without attribution the only way to find the culprit is
        // a manual re-trace of the whole log. Cheap enough to always keep on: one projection per action.
        var unitLedger = new List<UnitLedgerEntry>();
        var unitSnapshot = SnapshotUnits(context, replayGameId);
        GameActionDto? previousAction = null;
        // Units the live MoveArmy/MoveFleet endpoint destroyed via its own auto-combat while replaying the
        // CURRENT action, and (after the hand-off at the top of each iteration) while replaying the PREVIOUS
        // one. The "Battle" case consumes the latter so it doesn't destroy a second unit for a battle the
        // preceding move already resolved.
        var destroyedByCurrentMove = new List<(Nation Nation, UnitType UnitType, string TerritoryId)>();
        var destroyedByPreviousMove = new List<(Nation Nation, UnitType UnitType, string TerritoryId)>();

        try
        {
            for (int i = 0; i < actions.Count; i++)
            {
                // Unlike production (a fresh DI-scoped DbContext per HTTP request), this replay reuses one
                // long-lived DbContext across all actions via the controllers passed in. Clearing the change
                // tracker each iteration prevents stale/detached entity state from a much earlier action
                // leaking into a later, unrelated one's query results — the leading hypothesis for the
                // intermittent "No factory here" divergence this loop was built to catch (see [DIAG] output).
                context.ChangeTracker.Clear();
                unitSnapshot = RecordUnitChanges(context, replayGameId, unitSnapshot, previousAction, unitLedger);
                destroyedByPreviousMove = destroyedByCurrentMove;
                destroyedByCurrentMove = new List<(Nation Nation, UnitType UnitType, string TerritoryId)>();
                var action = actions[i];
                previousAction = action;
                // Skip system actions that are consequences or just informational
                if (action.ActionType == "JoinGame" || action.ActionType == "LeaveGame" ||
                    action.ActionType == "StartGame" ||
                    action.ActionType == "Investor" || action.ActionType == "InvestorBonus")
                {
                    if (onActionReplayed != null) await onActionReplayed(action, i, true);
                    continue;
                }

                // Setup user context for the action
                // For Investment actions, the controller checks game.ActingPlayerId, so we must
                // authenticate as whoever the replayed game thinks is acting, not the logged PlayerName.
                // For nation-based actions (Move, Production, etc.), auth is checked against the nation controller.
                Player? actingPlayer = null;
                var currentGameState = context.Games.Include(g => g.Players).Include(g => g.NationStates).First(g => g.Id == replayGameId);

                if (action.ActionType == "Investment")
                {
                    actingPlayer = context.Players.FirstOrDefault(p => p.GameId == replayGameId && ((p.BotName ?? p.UserId) == action.PlayerName || p.UserId == action.PlayerName));
                    if (actingPlayer != null && currentGameState.ActingPlayerId != actingPlayer.Id)
                    {
                        var gToUpdate = context.Games.First(g => g.Id == replayGameId);
                        gToUpdate.ActingPlayerId = actingPlayer.Id;
                        context.SaveChanges();
                    }
                }
                else if (action.ActionType == "SwissBankResponse" || action.ActionType == "Battle" || action.ActionType == "BattleResponse")
                {
                    actingPlayer = context.Players.FirstOrDefault(p => p.GameId == replayGameId && ((p.BotName ?? p.UserId) == action.PlayerName || p.UserId == action.PlayerName));
                }
                else if (action.Nation.HasValue)
                {
                    // Nation-based actions: auth checks the nation's ControllerId
                    var ns = currentGameState.NationStates.FirstOrDefault(n => n.Nation == action.Nation.Value);
                    if (ns?.ControllerId != null)
                    {
                        actingPlayer = currentGameState.Players.FirstOrDefault(p => p.Id == ns.ControllerId);
                    }
                }

                // Fallback: match by PlayerName
                if (actingPlayer == null)
                {
                    actingPlayer = context.Players.FirstOrDefault(p => p.GameId == replayGameId && ((p.BotName ?? p.UserId) == action.PlayerName || p.UserId == action.PlayerName));
                }

                if (actingPlayer != null)
                {
                    var repUserId = actingPlayer.UserId;
                    var repHttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext();
                    var repClaims = new List<System.Security.Claims.Claim> { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, repUserId) };
                    var repIdentity = new System.Security.Claims.ClaimsIdentity(repClaims, "TestAuthType");
                    repHttpContext.User = new System.Security.Claims.ClaimsPrincipal(repIdentity);

                    var repRouteData = new Microsoft.AspNetCore.Routing.RouteData();
                    var repActionDescriptor = new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor();
                    var repActionContext = new ActionContext(repHttpContext, repRouteData, repActionDescriptor);

                    gamesController.ControllerContext = new ControllerContext(repActionContext);
                    maneuverController.ControllerContext = new ControllerContext(repActionContext);
                }

                var actionNationStr = action.ActionType == "Move" ? (action.Nation?.ToString() ?? "Unknown") : "";
                var traceMsg = $"Replaying action: {action.ActionType} by {action.PlayerName} {actionNationStr}";
                _logger.LogDebug(traceMsg);

                IActionResult? result = null;
                try
                {
                    switch (action.ActionType)
                    {
                        case "Move":
                            var moveMeta = JsonSerializer.Deserialize<RondelMoveMetadata>(action.Metadata, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            var moveGame = context.Games.Include(g => g.NationStates).Include(g => g.Players).First(g => g.Id == replayGameId);
                            if (action.Nation.HasValue)
                            {
                                int maxAdvances = 6;
                                while (moveGame.CurrentTurnNation != action.Nation.Value && maxAdvances-- > 0)
                                {
                                    moveGame.AdvanceTurn();
                                }
                            }
                            var moveNs = moveGame.NationStates.First(n => n.Nation == action.Nation.Value);
                            moveNs.HasMovedThisTurn = false;
                            moveNs.HasImportedThisTurn = false;
                            moveNs.HasProducedThisTurn = false;
                            var moveCtrl = moveGame.Players.FirstOrDefault(p => p.Id == moveNs.ControllerId);
                            if (moveMeta != null)
                            {
                                if (moveMeta.CurrentSlot.HasValue && moveNs.RondelPosition != moveMeta.CurrentSlot.Value)
                                {
                                    moveNs.RondelPosition = moveMeta.CurrentSlot.Value;
                                }
                                if (moveCtrl != null && moveCtrl.Cash < moveMeta.Cost)
                                {
                                    moveCtrl.Cash = moveMeta.Cost;
                                }
                                // Bypass Swiss Bank intercept so the replayed Move executes immediately to its logged TargetSlot
                                moveGame.PendingSwissBankForceNation = action.Nation.Value;
                                foreach (var u in context.Units.Where(u => u.GameId == replayGameId && u.Nation == action.Nation.Value))
                                {
                                    u.HasMoved = false;
                                }
                                context.SaveChanges();
                            }
                            result = await gamesController.MoveNation(replayGameId, action.Nation.Value, moveMeta.TargetSlot);
                            break;
                        case "MoveArmy":
                            var maGame = context.Games.First(g => g.Id == replayGameId);
                            if (maGame.PendingBattleDefenders.Any())
                            {
                                maGame.PendingBattleTerritoryId = null;
                                maGame.PendingBattleAggressorNation = null;
                                maGame.PendingBattleDefenders = new List<Nation>();
                                context.Entry(maGame).Property(g => g.PendingBattleDefenders).IsModified = true;
                                context.Entry(maGame).State = EntityState.Modified;
                                context.SaveChanges();
                            }
                            var armyMeta = JsonSerializer.Deserialize<ActionMetadata>(action.Metadata, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            // Tightest possible match: same Nation/FromTerritory/UnitType/not-yet-moved AND the
                            // same pre-move IsHostile flag as the unit that actually moved in the original game.
                            // Added because two otherwise-identical units (same nation/type/territory) can differ
                            // in IsHostile, and that's exactly the signal needed to pick the right one when a
                            // nation moves multiple armies of the same type from the same territory in one turn.
                            // Falls straight through to the existing looser chain for older logged actions that
                            // predate this field (SourceIsHostile == null).
                            var armyUnitBySourceHostility = armyMeta.SourceIsHostile.HasValue
                                ? context.Units.FirstOrDefault(u => u.GameId == replayGameId && u.Nation == action.Nation && u.TerritoryId == armyMeta.FromTerritoryId && u.UnitType == UnitType.Army && !u.HasMoved && u.IsHostile == armyMeta.SourceIsHostile.Value)
                                : null;
                            var armyUnitExact = context.Units.FirstOrDefault(u => u.GameId == replayGameId && u.Nation == action.Nation && u.TerritoryId == armyMeta.FromTerritoryId && u.UnitType == UnitType.Army && !u.HasMoved);
                            var armyUnit = armyUnitBySourceHostility
                                ?? armyUnitExact
                                ?? context.Units.FirstOrDefault(u => u.GameId == replayGameId && u.Nation == action.Nation && u.TerritoryId == armyMeta.FromTerritoryId && u.UnitType == UnitType.Army)
                                ?? context.Units.FirstOrDefault(u => u.GameId == replayGameId && u.Nation == action.Nation && u.UnitType == UnitType.Army && !u.HasMoved)
                                ?? context.Units.FirstOrDefault(u => u.GameId == replayGameId && u.Nation == action.Nation && u.UnitType == UnitType.Army);
                            if (armyUnit != null && armyUnit != armyUnitBySourceHostility && armyUnit != armyUnitExact)
                            {
                                // The exact expected unit (right nation, right FROM territory, not yet moved this
                                // turn) wasn't found — this fell back to a less-precise match, which is a strong
                                // signal the board already diverged from the original before this action even ran.
                                _logger.LogDebug($"  [DIAG] MoveArmy fallback: no exact match for {action.Nation} army at '{armyMeta.FromTerritoryId}' (unmoved) — substituted unit {armyUnit.Id} currently at '{armyUnit.TerritoryId}' (HasMoved={armyUnit.HasMoved}) instead.");
                            }
                            if (armyUnit != null)
                            {
                                armyUnit.TerritoryId = armyMeta.FromTerritoryId;
                                armyUnit.HasMoved = false;
                                context.SaveChanges();
                            }
                            if (maGame.CurrentManeuverPhase != ManeuverPhase.Armies)
                            {
                                maGame.CurrentManeuverPhase = ManeuverPhase.Armies;
                                context.SaveChanges();
                            }
                            if (armyUnit != null) {
                                // Snapshot immediately around the live endpoint call so any unit its auto-combat
                                // destroys can be handed to the following logged "Battle" action (see that case).
                                var unitsBeforeArmyMove = SnapshotUnits(context, replayGameId);
                                var armyBattleTarget = FindAutoResolvedBattleTarget(actions, i, armyMeta.ToTerritoryId, action.Nation!.Value);
                                result = await maneuverController.MoveArmy(replayGameId, new MoveUnitRequest {
                                    UnitId = armyUnit.Id, DestinationId = armyMeta.ToTerritoryId, IsHostile = armyMeta.IsHostileMove ?? false,
                                    BattleTargetNation = armyBattleTarget?.DefenderNation, BattleTargetUnitType = armyBattleTarget?.DefenderUnitType,
                                    // Replay the journey the army actually made, not one the endpoint picks now.
                                    ConvoyFleetIds = ResolveLoggedConvoyFleets(context, replayGameId, action.Nation!.Value, armyMeta.RouteVia)
                                });
                                RecordMoveCombatDestructions(context, replayGameId, unitsBeforeArmyMove, armyMeta.ToTerritoryId, destroyedByCurrentMove);
                                if (result is BadRequestObjectResult)
                                {
                                    armyUnit.TerritoryId = armyMeta.ToTerritoryId;
                                    armyUnit.HasMoved = true;
                                    armyUnit.IsHostile = armyMeta.IsHostileMove ?? false;
                                    context.SaveChanges();
                                    result = new OkResult();
                                }
                                var tr = context.Units.Where(u => u.GameId == replayGameId && u.TerritoryId == armyMeta.ToTerritoryId).ToList();
                                _logger.LogDebug($"  -> MoveArmy {action.Nation} to {armyMeta.ToTerritoryId}. Units there now: {string.Join(", ", tr.Select(u => $"{u.UnitType} {u.Nation} {u.Id}"))}");
                                var mg = context.Games.First(g => g.Id == replayGameId);
                                if (mg.PendingBattleDefenders.Any())
                                {
                                    var nextAction = (i + 1 < actions.Count) ? actions[i + 1] : null;
                                    if (nextAction == null || (nextAction.ActionType != "Battle" && nextAction.ActionType != "BattleResponse"))
                                    {
                                        mg.PendingBattleTerritoryId = null;
                                        mg.PendingBattleAggressorNation = null;
                                        mg.PendingBattleDefenders = new List<Nation>();
                                        context.Entry(mg).Property(g => g.PendingBattleDefenders).IsModified = true;
                                        context.Entry(mg).State = EntityState.Modified;
                                        context.SaveChanges();
                                    }
                                }
                            } else {
                                // Silently continuing here (as this code used to) leaves `result` null, which
                                // none of the post-switch BadRequest/Forbid/Unauthorized checks catch — replay
                                // would carry on as if this action succeeded, quietly leaving the board short one
                                // army move for the rest of the game. That's the exact kind of silent divergence
                                // this whole replay mechanism was built to avoid, so fail loudly instead.
                                return new GameReplayResult
                                {
                                    Success = false,
                                    ErrorMessage = $"MoveArmy ({action.Id}): no {action.Nation} army found to move from '{armyMeta.FromTerritoryId}'. " +
                                        BuildMissingUnitDiagnostic(context, replayGameId, actions, action, UnitType.Army, unitLedger),
                                    FailedActionOrderIndex = action.OrderIndex,
                                    FailedActionType = action.ActionType
                                };
                            }
                            break;
                        case "MoveFleet":
                            var mfGame = context.Games.First(g => g.Id == replayGameId);
                            if (mfGame.PendingBattleDefenders.Any())
                            {
                                mfGame.PendingBattleTerritoryId = null;
                                mfGame.PendingBattleAggressorNation = null;
                                mfGame.PendingBattleDefenders = new List<Nation>();
                                context.Entry(mfGame).Property(g => g.PendingBattleDefenders).IsModified = true;
                                context.Entry(mfGame).State = EntityState.Modified;
                                context.SaveChanges();
                            }
                            var fleetMeta = JsonSerializer.Deserialize<ActionMetadata>(action.Metadata, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            // Tightest possible match first (see MoveArmy comment above for rationale); falls
                            // back to the existing heuristic chain unchanged for older logged actions that
                            // predate SourceIsHostile being recorded, or when no hostility-matching unit exists.
                            var fleetUnitBySourceHostility = fleetMeta.SourceIsHostile.HasValue
                                ? context.Units.FirstOrDefault(u => u.GameId == replayGameId && u.Nation == action.Nation && u.TerritoryId == fleetMeta.FromTerritoryId && u.UnitType == UnitType.Fleet && !u.HasMoved && u.IsHostile == fleetMeta.SourceIsHostile.Value)
                                : null;
                            var fleetUnit = fleetUnitBySourceHostility
                                ?? context.Units.FirstOrDefault(u => u.GameId == replayGameId && u.Nation == action.Nation && u.TerritoryId == fleetMeta.FromTerritoryId && u.UnitType == UnitType.Fleet && !u.HasMoved)
                                ?? context.Units.FirstOrDefault(u => u.GameId == replayGameId && u.Nation == action.Nation && u.TerritoryId == fleetMeta.FromTerritoryId && u.UnitType == UnitType.Fleet)
                                ?? context.Units.FirstOrDefault(u => u.GameId == replayGameId && u.Nation == action.Nation && u.UnitType == UnitType.Fleet && !u.HasMoved)
                                ?? context.Units.FirstOrDefault(u => u.GameId == replayGameId && u.Nation == action.Nation && u.UnitType == UnitType.Fleet);
                            if (fleetUnit != null)
                            {
                                fleetUnit.TerritoryId = fleetMeta.FromTerritoryId;
                                fleetUnit.HasMoved = false;
                                context.SaveChanges();
                            }
                            if (mfGame.CurrentManeuverPhase != ManeuverPhase.Fleets)
                            {
                                mfGame.CurrentManeuverPhase = ManeuverPhase.Fleets;
                                context.SaveChanges();
                            }
                            if (fleetUnit != null) {
                                var allInTerr = context.Units.Where(u => u.GameId == replayGameId && u.TerritoryId == fleetMeta.ToTerritoryId).ToList();
                                _logger.LogDebug($"  -> MoveFleet {action.Nation} to {fleetMeta.ToTerritoryId}. IsHostile={fleetMeta.IsHostileMove}. Units there: {string.Join(", ", allInTerr.Select(u => $"{u.UnitType} {u.Nation} {u.Id}"))}");
                                // Snapshot immediately around the live endpoint call so any unit its auto-combat
                                // destroys can be handed to the following logged "Battle" action (see that case).
                                var unitsBeforeFleetMove = SnapshotUnits(context, replayGameId);
                                var fleetBattleTarget = FindAutoResolvedBattleTarget(actions, i, fleetMeta.ToTerritoryId, action.Nation!.Value);
                                result = await maneuverController.MoveFleet(replayGameId, new MoveUnitRequest {
                                    UnitId = fleetUnit.Id, DestinationId = fleetMeta.ToTerritoryId, IsHostile = fleetMeta.IsHostileMove ?? false,
                                    BattleTargetNation = fleetBattleTarget?.DefenderNation, BattleTargetUnitType = fleetBattleTarget?.DefenderUnitType
                                });
                                RecordMoveCombatDestructions(context, replayGameId, unitsBeforeFleetMove, fleetMeta.ToTerritoryId, destroyedByCurrentMove);
                                if (result is BadRequestObjectResult)
                                {
                                    fleetUnit.TerritoryId = fleetMeta.ToTerritoryId;
                                    fleetUnit.HasMoved = true;
                                    fleetUnit.IsHostile = fleetMeta.IsHostileMove ?? false;
                                    context.SaveChanges();
                                    result = new OkResult();
                                }
                                var mg = context.Games.First(g => g.Id == replayGameId);
                                _logger.LogDebug($"  -> After MoveFleet, PendingBattle={mg.PendingBattleTerritoryId}, Defenders={string.Join(",", mg.PendingBattleDefenders)}");
                                if (mg.PendingBattleDefenders.Any())
                                {
                                    var nextAction = (i + 1 < actions.Count) ? actions[i + 1] : null;
                                    if (nextAction == null || (nextAction.ActionType != "Battle" && nextAction.ActionType != "BattleResponse"))
                                    {
                                        mg.PendingBattleTerritoryId = null;
                                        mg.PendingBattleAggressorNation = null;
                                        mg.PendingBattleDefenders = new List<Nation>();
                                        context.Entry(mg).Property(g => g.PendingBattleDefenders).IsModified = true;
                                        context.Entry(mg).State = EntityState.Modified;
                                        context.SaveChanges();
                                    }
                                }
                            } else {
                                return new GameReplayResult
                                {
                                    Success = false,
                                    ErrorMessage = $"MoveFleet ({action.Id}): no {action.Nation} fleet found to move from '{fleetMeta.FromTerritoryId}'. " +
                                        BuildMissingUnitDiagnostic(context, replayGameId, actions, action, UnitType.Fleet, unitLedger),
                                    FailedActionOrderIndex = action.OrderIndex,
                                    FailedActionType = action.ActionType
                                };
                            }
                            break;
                        case "ToggleHostility":
                            var hostMeta = JsonSerializer.Deserialize<HostilityMetadata>(action.Metadata, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            // Disambiguation tiers, strongest signal first (existing unit properties only):
                            // 1. a unit whose IsHostile actually differs from the logged result AND that hasn't
                            //    moved yet this phase — bots only log a toggle for a unit that stayed put, so the
                            //    toggled unit is by definition one the replay hasn't moved;
                            // 2. any unit whose hostility still needs flipping;
                            // 3. any matching unit at all (the flag may already hold the logged value).
                            var unit = context.Units.FirstOrDefault(u => u.GameId == replayGameId && u.Nation == action.Nation && u.TerritoryId == hostMeta.TerritoryId && u.UnitType == hostMeta.UnitType && u.IsHostile != hostMeta.IsHostile && !u.HasMoved)
                                ?? context.Units.FirstOrDefault(u => u.GameId == replayGameId && u.Nation == action.Nation && u.TerritoryId == hostMeta.TerritoryId && u.UnitType == hostMeta.UnitType && u.IsHostile != hostMeta.IsHostile)
                                ?? context.Units.FirstOrDefault(u => u.GameId == replayGameId && u.Nation == action.Nation && u.TerritoryId == hostMeta.TerritoryId && u.UnitType == hostMeta.UnitType);
                            if (unit != null) {
                                unit.IsHostile = hostMeta.IsHostile;
                                context.SaveChanges();
                                // See the matching comment on the "Production"/"Import" cases: without this,
                                // a later replay of the replay target's own action log has no record that
                                // this unit's hostility flag changed.
                                var hostGame = context.Games.First(g => g.Id == replayGameId);
                                GameLogger.LogHostilityToggle(context, hostGame, hostMeta.UnitType, hostMeta.TerritoryId, hostMeta.IsHostile, action.Nation!.Value, action.PlayerName);
                            }
                            result = new OkResult();
                            break;
                        case "BattleResponse":
                            var brMeta = JsonSerializer.Deserialize<ActionMetadata>(action.Metadata, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            var bg = context.Games.First(g => g.Id == replayGameId);
                            if (bg.PendingBattleTerritoryId != null)
                            {
                                result = await maneuverController.BattleResponse(replayGameId, new BattleResponseRequest { IsFight = brMeta?.IsHostileMove ?? false, Nation = action.Nation });
                            }
                            else
                            {
                                result = new OkResult();
                            }
                            break;
                        case "Battle":
                            var bMeta = JsonSerializer.Deserialize<ActionMetadata>(action.Metadata, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (bMeta == null || string.IsNullOrEmpty(bMeta.TerritoryId) || !bMeta.AggressorNation.HasValue || !bMeta.DefenderNation.HasValue)
                            {
                                return new GameReplayResult
                                {
                                    Success = false,
                                    ErrorMessage = $"Battle ({action.Id}): metadata is missing TerritoryId/AggressorNation/DefenderNation.",
                                    FailedActionOrderIndex = action.OrderIndex,
                                    FailedActionType = action.ActionType
                                };
                            }
                            // The preceding MoveArmy/MoveFleet replay call above already went through the real
                            // ManeuverController endpoint, which AUTO-RESOLVES combat as a side effect when the
                            // move is hostile and there is exactly one defending nation (see MoveArmy/MoveFleet's
                            // "Auto-resolve hostile battle if there is only 1 valid target" branches). In that
                            // case this logged "Battle" action is nothing more than the record of the very
                            // destruction the endpoint just performed — re-applying it here destroys a SECOND,
                            // innocent pair of units.
                            //
                            // Checking "is a matching unit still standing?" is NOT enough to tell the two cases
                            // apart: when a nation has two identical units in the same territory (e.g. two fleets
                            // that both moved into NorthAtlantic), one is destroyed by the endpoint and the other
                            // is still sitting right there, matching every criterion — so the naive lookup below
                            // happily removes the survivor too. That was the actual, long-hunted cause of the
                            // intermittent "nation has zero units of that type anywhere" replay failure: a
                            // silent -1 per auto-resolved battle, compounding until some later MoveArmy/MoveFleet
                            // had nothing left to move.
                            //
                            // So consult what the preceding replayed move's endpoint call ACTUALLY destroyed
                            // (recorded by RecordMoveCombatDestructions) and skip each side already accounted
                            // for, consuming the record so a second battle at the same territory can't reuse it.
                            // Only MoveArmy/MoveFleet feed this list — a preceding "Battle" action's own
                            // removals are a different battle and must never be consumed here.
                            bool aggressorAlreadyDestroyed =
                                ConsumeDestroyedUnit(destroyedByPreviousMove, bMeta.TerritoryId, bMeta.AggressorNation.Value, bMeta.UnitType);
                            bool defenderAlreadyDestroyed =
                                ConsumeDestroyedUnit(destroyedByPreviousMove, bMeta.TerritoryId, bMeta.DefenderNation.Value, bMeta.DefenderUnitType);

                            // The aggressor is, by definition, the unit that just performed the hostile move
                            // which triggered this battle — so it should have HasMoved==true at this point in
                            // replay (the preceding MoveArmy/MoveFleet action set that). The defender is
                            // whichever nation's unit was sitting there already, i.e. HasMoved==false. When a
                            // territory has 2+ otherwise-identical units of the aggressor/defender nation+type,
                            // this picks the one consistent with its role instead of an arbitrary match — the
                            // same kind of existing-property disambiguation already used for MoveArmy/MoveFleet,
                            // reusing HasMoved rather than adding new schema.
                            var aggUnit = aggressorAlreadyDestroyed ? null
                                : context.Units.FirstOrDefault(u => u.GameId == replayGameId && u.TerritoryId == bMeta.TerritoryId && u.Nation == bMeta.AggressorNation.Value && (!bMeta.UnitType.HasValue || u.UnitType == bMeta.UnitType.Value) && u.HasMoved)
                                ?? context.Units.FirstOrDefault(u => u.GameId == replayGameId && u.TerritoryId == bMeta.TerritoryId && u.Nation == bMeta.AggressorNation.Value && (!bMeta.UnitType.HasValue || u.UnitType == bMeta.UnitType.Value));
                            var defUnit = defenderAlreadyDestroyed ? null
                                : context.Units.FirstOrDefault(u => u.GameId == replayGameId && u.TerritoryId == bMeta.TerritoryId && u.Nation == bMeta.DefenderNation.Value && (!bMeta.DefenderUnitType.HasValue || u.UnitType == bMeta.DefenderUnitType.Value) && !u.HasMoved)
                                ?? context.Units.FirstOrDefault(u => u.GameId == replayGameId && u.TerritoryId == bMeta.TerritoryId && u.Nation == bMeta.DefenderNation.Value && (!bMeta.DefenderUnitType.HasValue || u.UnitType == bMeta.DefenderUnitType.Value));
                            // Each side is removed independently: if only one side was already resolved above,
                            // its still-present counterpart must still be removed, not left stranded on the board.
                            if (aggUnit != null) context.Units.Remove(aggUnit);
                            if (defUnit != null) context.Units.Remove(defUnit);
                            if (aggUnit != null || defUnit != null)
                            {
                                context.SaveChanges();
                                // Only when this case actually performed a removal — if it was fully
                                // auto-resolved by the preceding move (aggressorAlreadyDestroyed AND
                                // defenderAlreadyDestroyed), the endpoint that did that already called
                                // GameLogger.LogBattleDestruction itself as a natural side effect; logging it
                                // again here would duplicate that entry. See the matching comment on the
                                // "Production"/"Import" cases for why the replay target's own log needs this
                                // at all (a later replay of THAT game's log has to reconstruct these
                                // destructions from somewhere).
                                var battleGame = context.Games.First(g => g.Id == replayGameId);
                                GameLogger.LogBattleDestruction(context, battleGame, bMeta.UnitType ?? UnitType.Army, bMeta.DefenderNation.Value, bMeta.DefenderUnitType ?? UnitType.Army, bMeta.TerritoryId, bMeta.AggressorNation.Value, action.PlayerName);
                            }
                            result = new OkResult();
                            break;

                        case "FlagPlacement":
                            var fpMeta = JsonSerializer.Deserialize<FlagPlacementMetadata>(action.Metadata, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (fpMeta != null && !string.IsNullOrEmpty(fpMeta.TerritoryId))
                            {
                                var fpTerr = context.TerritoryStates.FirstOrDefault(ts => ts.GameId == replayGameId && ts.TerritoryId == fpMeta.TerritoryId);
                                // Only when the control change has not already happened. Flag placement is a
                                // DERIVED entry: UpdateTerritoryControl runs inside the real Maneuver endpoints
                                // and both moves the flag and logs it as a natural side effect of the preceding
                                // MoveArmy/MoveFleet action this replay just dispatched. Re-applying it here
                                // would be a no-op write, but the log call is not - it produced a second,
                                // identical entry in the replay target's log a moment after the first. Same
                                // reasoning as the "Battle" case above.
                                //
                                // The write and the log still run when the controller does NOT already match:
                                // that is a change no replayed move accounted for, and dropping it would leave
                                // the board wrong. See the "Production"/"Import" cases for why the replay
                                // target's own log needs these entries at all.
                                if (fpTerr != null && fpTerr.Controller != fpMeta.NewController)
                                {
                                    fpTerr.Controller = fpMeta.NewController;
                                    var fpGame = context.Games.First(g => g.Id == replayGameId);
                                    GameLogger.LogTerritoryControlChange(context, fpGame, fpMeta.TerritoryId, fpMeta.OldController, fpMeta.NewController, action.PlayerName);
                                    // Saved after the log entry is added, not before: GameLogger only tracks
                                    // the new row, so a SaveChanges that runs first persists the control
                                    // change and leaves the entry to be picked up by whatever action happens
                                    // to save next - or dropped entirely when this is the last one.
                                    context.SaveChanges();
                                }
                            }
                            result = new OkResult();
                            break;
                        case "Production":
                            // Unlike most cases, this does NOT call the real ExecuteProduction endpoint. That
                            // endpoint deterministically DERIVES which units to produce from the CURRENT board
                            // state (which factories are unblockaded, current unit counts vs. the nation's cap)
                            // rather than reading the logged ProductionMetadata.Units list at all — so if the
                            // replay board has already drifted even slightly from the original by this point
                            // (e.g. from an earlier ambiguous unit match), production would silently produce a
                            // DIFFERENT set of units than the original game did, compounding a small divergence
                            // into a much larger one every time it runs for the rest of the game. Production
                            // logs the exact units it created (same as "Import" does), so — like Import — just
                            // create exactly those, instead of re-deriving from replay-time state.
                            var prodGame = context.Games.First(g => g.Id == replayGameId);
                            if (action.Nation.HasValue)
                            {
                                prodGame.CurrentTurnNation = action.Nation.Value;
                            }
                            var prodNs = context.NationStates.First(n => n.GameId == replayGameId && n.Nation == prodGame.CurrentTurnNation);
                            var prodMeta = JsonSerializer.Deserialize<ProductionMetadata>(action.Metadata, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (prodMeta?.Units != null && prodMeta.Units.Any())
                            {
                                prodNs.HasProducedThisTurn = true;
                                foreach (var uInfo in prodMeta.Units)
                                {
                                    context.Units.Add(new Unit
                                    {
                                        Id = Guid.NewGuid(),
                                        GameId = replayGameId,
                                        Nation = prodGame.CurrentTurnNation,
                                        TerritoryId = uInfo.TerritoryId,
                                        UnitType = uInfo.UnitType,
                                        IsHostile = false
                                    });
                                }
                                // Without this, the replay target's OWN action log has no record of where
                                // these units came from — harmless for a one-off replay/import, but it means
                                // THAT game's log is no longer internally self-consistent, so a LATER replay
                                // of it (e.g. "Start Replay" on an already-imported game) has nothing to
                                // reconstruct these units from and fails as soon as one of them needs to move.
                                GameLogger.LogProduction(context, prodGame, prodMeta.Units.Count, prodMeta.Units.Select(u => (u.UnitType, u.TerritoryId)), prodGame.CurrentTurnNation, action.PlayerName);
                            }
                            context.SaveChanges();
                            result = new OkResult();
                            break;
                        case "Taxation":
                            var taxGame = context.Games.First(g => g.Id == replayGameId);
                            if (action.Nation.HasValue)
                            {
                                taxGame.CurrentTurnNation = action.Nation.Value;
                            }
                            var taxNs = context.NationStates.First(n => n.GameId == replayGameId && n.Nation == taxGame.CurrentTurnNation);
                            if (taxNs.RondelPosition != RondelData.TaxationSlot)
                            {
                                taxNs.RondelPosition = RondelData.TaxationSlot;
                            }
                            var taxMeta = JsonSerializer.Deserialize<TaxationMetadata>(action.Metadata, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            var oldPower = taxNs.Power;
                            var oldTreasury = taxNs.Treasury;
                            var oldTaxRevenue = taxNs.TaxRevenue;
                            if (taxMeta != null && oldPower + taxMeta.PowerGain >= GameConstants.MaxPowerPoints)
                            {
                                taxNs.Power = GameConstants.MaxPowerPoints;
                            }
                            context.SaveChanges();
                            result = await gamesController.ExecuteTaxation(replayGameId);
                            if (taxMeta != null)
                            {
                                taxNs.Power = Math.Min(GameConstants.MaxPowerPoints, oldPower + taxMeta.PowerGain);
                                taxGame.Status = (taxNs.Power >= GameConstants.MaxPowerPoints) ? GameStatus.Finished : GameStatus.InProgress;
                                int withRevenue = oldTreasury + taxMeta.TotalRevenue;
                                int actualPay = Math.Min(withRevenue, taxMeta.SoldiersPay);
                                int afterPay = withRevenue - actualPay;
                                int actualBonus = Math.Min(afterPay, taxMeta.Bonus);
                                taxNs.Treasury = Math.Max(0, afterPay - actualBonus);
                                // ExecuteTaxation derives TaxRevenue from the CURRENT board (factories not
                                // blockaded + flags controlled), so it drifts exactly like Production used to.
                                // The logged TotalRevenue is that same number, recorded, so reconstruct both
                                // fields from it directly: TaxRevenue = what this taxation raised, and
                                // PreviousTaxRevenue = whatever TaxRevenue held before it (mirroring
                                // TaxationHelper.ApplyTaxation). This replaces an older
                                // `Math.Max(PreviousTaxRevenue, TotalRevenue)` guess that had no counterpart in
                                // the real rules and left TaxRevenue itself at its derived (possibly wrong)
                                // value — which then feeds the next taxation's variant bonus tier.
                                taxNs.PreviousTaxRevenue = oldTaxRevenue;
                                taxNs.TaxRevenue = taxMeta.TotalRevenue;
                                context.SaveChanges();
                            }
                            break;
                        case "Factory":
                            var fMeta = JsonSerializer.Deserialize<ActionMetadata>(action.Metadata, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            var factGame = context.Games.First(g => g.Id == replayGameId);
                            // BuildFactory acts on game.CurrentTurnNation, and so does everything below. The
                            // logged action already names the nation that built, so pin it rather than trusting
                            // whatever CurrentTurnNation happens to hold — exactly as the Production/Taxation/
                            // Import cases do. Without this, any turn-pointer drift would build the factory for
                            // (and force RondelPosition/Treasury onto) the wrong nation's state.
                            if (action.Nation.HasValue)
                            {
                                factGame.CurrentTurnNation = action.Nation.Value;
                            }
                            var factNs = context.NationStates.First(n => n.GameId == replayGameId && n.Nation == factGame.CurrentTurnNation);
                            if (factNs.RondelPosition != RondelData.FactorySlot)
                            {
                                factNs.RondelPosition = RondelData.FactorySlot;
                                context.SaveChanges();
                            }
                            if (factNs.Treasury < 5)
                            {
                                factNs.Treasury = 5;
                                context.SaveChanges();
                            }
                            var hostileInFactoryTerr = context.Units
                                .Where(u => u.GameId == replayGameId && u.TerritoryId == fMeta.TerritoryId && u.Nation != factGame.CurrentTurnNation && u.UnitType == UnitType.Army && u.IsHostile)
                                .ToList();
                            foreach (var h in hostileInFactoryTerr)
                            {
                                h.IsHostile = false;
                            }
                            if (hostileInFactoryTerr.Any())
                            {
                                context.SaveChanges();
                            }
                            result = await gamesController.BuildFactory(replayGameId, fMeta.TerritoryId);
                            // Un-hostiling those enemy armies is only a way to get past BuildFactory's blockade
                            // check for a build the original game already performed — it is NOT something that
                            // happened in the original. Leaving them peaceful would silently change later
                            // production blockades, taxation revenue and battle resolution, so restore the flag.
                            if (hostileInFactoryTerr.Any())
                            {
                                foreach (var h in hostileInFactoryTerr)
                                {
                                    if (context.Entry(h).State != EntityState.Detached && context.Units.Any(u => u.Id == h.Id))
                                    {
                                        h.IsHostile = true;
                                    }
                                }
                                context.SaveChanges();
                            }
                            break;

                        case "DestroyFactory":
                            var dfMeta = JsonSerializer.Deserialize<ActionMetadata>(action.Metadata, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            var dfGame = context.Games.First(g => g.Id == replayGameId);
                            // DestroyFactory validates and sacrifices armies belonging to game.CurrentTurnNation.
                            // The logged action names the destroying nation, so pin it (same reason as the
                            // Factory case above) — otherwise turn-pointer drift makes this sacrifice three of
                            // some OTHER nation's armies, destroying units that survived in the original game.
                            if (action.Nation.HasValue)
                            {
                                dfGame.CurrentTurnNation = action.Nation.Value;
                                context.SaveChanges();
                            }
                            // Prefer armies already flagged hostile: entering a foreign home province to destroy
                            // a factory is typically a hostile act, so the three that actually did it in the
                            // original were often hostile. Picking those first avoids consuming peaceful armies
                            // stationed in the same territory for some other reason.
                            //
                            // That alone isn't enough when the territory was entered peacefully throughout (e.g.
                            // an army already sat there undefended from an earlier turn, then more armies moved
                            // in peacefully this turn to finish the job) — every candidate is equally IsHostile
                            // there, so break remaining ties by HasMoved: the armies that actually triggered the
                            // destruction necessarily just moved this turn, while an army that's been sitting at
                            // the territory since before this turn (and so wasn't part of the sacrifice) has not.
                            var dfArmies = context.Units.Where(u => u.GameId == replayGameId && u.TerritoryId == dfMeta.TerritoryId && u.Nation == dfGame.CurrentTurnNation && u.UnitType == UnitType.Army)
                                .OrderByDescending(u => u.IsHostile)
                                .ThenByDescending(u => u.HasMoved)
                                .Take(ManeuverRules.DestroyFactoryArmyCost).ToList();
                            while (dfArmies.Count < ManeuverRules.DestroyFactoryArmyCost)
                            {
                                var extraArmy = new Unit
                                {
                                    Id = Guid.NewGuid(),
                                    GameId = replayGameId,
                                    Nation = dfGame.CurrentTurnNation,
                                    TerritoryId = dfMeta.TerritoryId,
                                    UnitType = UnitType.Army,
                                    IsHostile = true
                                };
                                context.Units.Add(extraArmy);
                                dfArmies.Add(extraArmy);
                            }
                            var tDef = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == dfMeta.TerritoryId);
                            if (tDef?.Nation.HasValue == true)
                            {
                                var defUnits = context.Units.Where(u => u.GameId == replayGameId && u.TerritoryId == dfMeta.TerritoryId && u.Nation == tDef.Nation.Value).ToList();
                                context.Units.RemoveRange(defUnits);
                            }
                            context.SaveChanges();
                            result = await maneuverController.DestroyFactory(replayGameId, new DestroyFactoryRequest { TerritoryId = dfMeta.TerritoryId, UnitIds = dfArmies.Select(u => u.Id).ToList() });
                            break;
                        case "Investment":
                            var invGame = context.Games.First(g => g.Id == replayGameId);
                            if (!invGame.IsInvestorTurn)
                            {
                                result = new OkResult();
                                break;
                            }
                            var invMeta = !string.IsNullOrEmpty(action.Metadata) ? JsonSerializer.Deserialize<InvestmentMetadata>(action.Metadata, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) : null;
                            if (invMeta != null && invMeta.Cost > 0)
                            {
                                Enum.TryParse<Nation>(invMeta.Nation, out var invNation);
                                var actualInvestingPlayer = context.Players.FirstOrDefault(p => p.GameId == replayGameId && ((p.BotName ?? p.UserId) == action.PlayerName || p.UserId == action.PlayerName))
                                    ?? context.Players.FirstOrDefault(p => p.Id == invGame.ActingPlayerId);
                                if (actualInvestingPlayer != null)
                                {
                                    invGame.ActingPlayerId = actualInvestingPlayer.Id;
                                }
                                var bondToBuy = context.Bonds.FirstOrDefault(b => b.GameId == replayGameId && b.Nation == invNation && b.Cost == invMeta.Cost && b.HolderId == null)
                                    ?? context.Bonds.FirstOrDefault(b => b.GameId == replayGameId && b.Nation == invNation && b.Cost == invMeta.Cost);
                                if (bondToBuy != null && bondToBuy.HolderId != null)
                                {
                                    bondToBuy.HolderId = null;
                                }

                                Guid? tradeInId = null;
                                int netCost = invMeta.Cost.Value;
                                if (invMeta.TradeInCost > 0 && actualInvestingPlayer != null)
                                {
                                    var tradeIn = context.Bonds.FirstOrDefault(b => b.GameId == replayGameId && b.Nation == invNation && b.Cost == invMeta.TradeInCost && b.HolderId == actualInvestingPlayer.Id)
                                        ?? context.Bonds.FirstOrDefault(b => b.GameId == replayGameId && b.Nation == invNation && b.Cost == invMeta.TradeInCost && b.HolderId != null);
                                    if (tradeIn != null)
                                    {
                                        tradeIn.HolderId = actualInvestingPlayer.Id;
                                        tradeInId = tradeIn.Id;
                                    }
                                    netCost = invMeta.Cost.Value - invMeta.TradeInCost.Value;
                                }
                                if (actualInvestingPlayer != null && actualInvestingPlayer.Cash < netCost)
                                {
                                    actualInvestingPlayer.Cash = netCost;
                                }
                                context.SaveChanges();

                                var investorPlayerLog = context.Players.FirstOrDefault(p => p.Id == invGame.ActingPlayerId);
                                _logger.LogDebug($"  -> Investment: Player={investorPlayerLog?.UserId} Cash={investorPlayerLog?.Cash} BondCost={invMeta.Cost} TradeIn={invMeta.TradeInCost} TradeInId={tradeInId} Nation={invMeta.Nation}");
                                result = await gamesController.PerformInvestment(replayGameId, new GamesController.InvestmentActionDto { ActionType = "Buy", BondId = bondToBuy?.Id, TradeInBondId = tradeInId });
                            }
                            else
                            {
                                result = await gamesController.PerformInvestment(replayGameId, new GamesController.InvestmentActionDto { ActionType = "Pass" });
                            }
                            break;
                        case "SwissBankResponse":
                            result = new OkResult();
                            break;
                        case "EndPhase":
                        case "AutoEndPhase":
                            var phaseMeta = JsonSerializer.Deserialize<PhaseMetadata>(action.Metadata, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            var phaseGame = context.Games.First(g => g.Id == replayGameId);
                            if (phaseMeta != null)
                            {
                                await maneuverController.UpdateTerritoryControl(phaseGame);
                                if (phaseMeta.PhaseName == "Fleets" && phaseGame.CurrentManeuverPhase == ManeuverPhase.Fleets)
                                {
                                    phaseGame.CurrentManeuverPhase = ManeuverPhase.Armies;
                                }
                                else if (phaseMeta.PhaseName == "Armies" && phaseGame.CurrentManeuverPhase == ManeuverPhase.Armies)
                                {
                                    phaseGame.CurrentManeuverPhase = ManeuverPhase.None;
                                }
                                phaseGame.PendingBattleTerritoryId = null;
                                phaseGame.PendingBattleAggressorNation = null;
                                phaseGame.PendingBattleDefenders = new List<Nation>();
                                context.Entry(phaseGame).Property(g => g.PendingBattleDefenders).IsModified = true;
                                context.Entry(phaseGame).State = EntityState.Modified;
                                context.SaveChanges();
                            }
                            result = new OkResult();
                            break;
                        case "AutoSkipPhase":
                            var aspMeta = JsonSerializer.Deserialize<PhaseMetadata>(action.Metadata, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (aspMeta?.PhaseName == "Turn")
                            {
                                var aspGame = context.Games.First(g => g.Id == replayGameId);
                                aspGame.AdvanceTurn();
                                context.SaveChanges();
                            }
                            result = new OkResult();
                            break;
                        case "EndTurn":
                            var etGame = context.Games.First(g => g.Id == replayGameId);
                            await maneuverController.UpdateTerritoryControl(etGame);
                            etGame.PendingBattleTerritoryId = null;
                            etGame.PendingBattleAggressorNation = null;
                            etGame.PendingBattleDefenders = new List<Nation>();
                            etGame.CurrentManeuverPhase = ManeuverPhase.None;
                            context.Entry(etGame).Property(g => g.PendingBattleDefenders).IsModified = true;
                            context.Entry(etGame).State = EntityState.Modified;
                            context.SaveChanges();
                            result = await gamesController.EndTurn(replayGameId);
                            break;
                        case "Import":
                            var impGame = context.Games.First(g => g.Id == replayGameId);
                            if (action.Nation.HasValue)
                            {
                                impGame.CurrentTurnNation = action.Nation.Value;
                            }
                            var impNs = context.NationStates.First(n => n.GameId == replayGameId && n.Nation == impGame.CurrentTurnNation);
                            impNs.HasImportedThisTurn = true;
                            var impMeta = JsonSerializer.Deserialize<ImportMetadata>(action.Metadata, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (impMeta?.Units != null && impMeta.Units.Any())
                            {
                                impNs.Treasury = Math.Max(0, impNs.Treasury - impMeta.Units.Count);
                                foreach (var uInfo in impMeta.Units)
                                {
                                    var newUnit = new Unit
                                    {
                                        Id = Guid.NewGuid(),
                                        GameId = replayGameId,
                                        Nation = impGame.CurrentTurnNation,
                                        TerritoryId = uInfo.TerritoryId,
                                        UnitType = uInfo.UnitType,
                                        IsHostile = false
                                    };
                                    context.Units.Add(newUnit);
                                }
                                // See the matching comment in the "Production" case above — without this, a
                                // later replay of the replay target's OWN action log has no record of where
                                // these units came from.
                                GameLogger.LogImport(context, impGame, impMeta.Units.Count, impMeta.Units.Select(u => (u.UnitType, u.TerritoryId)), impGame.CurrentTurnNation, action.PlayerName);
                            }
                            context.SaveChanges();
                            result = new OkResult();
                            break;
                    }
                }
                catch (Exception ex)
                {
                    return new GameReplayResult
                    {
                        Success = false,
                        ErrorMessage = $"Failed to replay action {action.ActionType} ({action.Id}): {ex.Message}",
                        FailedActionOrderIndex = action.OrderIndex,
                        FailedActionType = action.ActionType
                    };
                }

                if (result is BadRequestObjectResult br)
                {
                    var replayGame = context.Games.First(g => g.Id == replayGameId);
                    var allUnits = context.Units.Where(u => u.GameId == replayGameId).ToList();
                    _logger.LogDebug($"FAILED with {br.Value}. Units: {string.Join(", ", allUnits.Select(u => $"{u.UnitType} {u.Nation} in {u.TerritoryId} (Hostile={u.IsHostile})"))}");

                    // Diagnostic for the intermittent "No factory here" DestroyFactory replay failure: trace
                    // every Factory/DestroyFactory action against this exact territory from the ORIGINAL log
                    // (in order), plus the replay's current TerritoryState for it, to distinguish "the build
                    // for this territory never got replayed" from "this territory's factory was already
                    // destroyed earlier in the replay" without needing another repro cycle.
                    if ((action.ActionType == "Factory" || action.ActionType == "DestroyFactory") && !string.IsNullOrEmpty(action.Metadata))
                    {
                        var diagMeta = JsonSerializer.Deserialize<ActionMetadata>(action.Metadata, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (diagMeta?.TerritoryId != null)
                        {
                            var history = actions.Where(a => (a.ActionType == "Factory" || a.ActionType == "DestroyFactory") && !string.IsNullOrEmpty(a.Metadata))
                                .Select(a => new { a.OrderIndex, a.ActionType, a.PlayerName, Meta = JsonSerializer.Deserialize<ActionMetadata>(a.Metadata, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) })
                                .Where(x => x.Meta?.TerritoryId == diagMeta.TerritoryId)
                                .ToList();
                            _logger.LogDebug($"  [DIAG] Original-log history for territory '{diagMeta.TerritoryId}' ({history.Count} entries): {string.Join(" | ", history.Select(h => $"#{h.OrderIndex} {h.ActionType} by {h.PlayerName}"))}");
                            var diagTState = context.TerritoryStates.FirstOrDefault(ts => ts.GameId == replayGameId && ts.TerritoryId == diagMeta.TerritoryId);
                            _logger.LogDebug($"  [DIAG] Replay's current TerritoryState for '{diagMeta.TerritoryId}': HasFactory={diagTState?.HasFactory}, this action's OrderIndex=#{action.OrderIndex}");
                        }
                    }

                    return new GameReplayResult
                    {
                        Success = false,
                        ErrorMessage = $"Action {action.ActionType} ({action.Id}) returned BadRequest: {br.Value}",
                        FailedActionOrderIndex = action.OrderIndex,
                        FailedActionType = action.ActionType
                    };
                }
                if (result is ForbidResult || (result as StatusCodeResult)?.StatusCode == 403)
                {
                    var curGame = context.Games.Include(g => g.Players).Include(g => g.NationStates).First(g => g.Id == replayGameId);
                    var curActPlayer = curGame.Players.FirstOrDefault(p => p.Id == curGame.ActingPlayerId);

                    // BattleResponse/Battle/SwissBankResponse don't authorize via ActingPlayerId at all (that's
                    // Investment-only) — they check whether the calling user controls one of the pending battle's
                    // defending nations. Printing ActingPlayerId for those is actively misleading (always shows
                    // null/unrelated), so show the mechanism that's actually being checked instead.
                    string extraDiag = "";
                    if (action.ActionType == "BattleResponse" || action.ActionType == "Battle")
                    {
                        var defenderInfo = curGame.PendingBattleDefenders.Select(n =>
                        {
                            var ns = curGame.NationStates.FirstOrDefault(x => x.Nation == n);
                            var ctrl = curGame.Players.FirstOrDefault(p => p.Id == ns?.ControllerId);
                            return $"{n} (ControllerId={ns?.ControllerId}, ControllerUserId={ctrl?.UserId ?? "null"})";
                        });
                        extraDiag = $" PendingBattleTerritoryId={curGame.PendingBattleTerritoryId}, PendingBattleAggressorNation={curGame.PendingBattleAggressorNation}, PendingBattleDefenders=[{string.Join(", ", defenderInfo)}].";
                    }

                    return new GameReplayResult
                    {
                        Success = false,
                        ErrorMessage = $"Action {action.ActionType} ({action.Id}) returned Forbid. Expected Player: {action.PlayerName}, Actual ActingPlayer: {curActPlayer?.UserId ?? "null"} (ActingPlayerId: {curGame.ActingPlayerId}).{extraDiag}",
                        FailedActionOrderIndex = action.OrderIndex,
                        FailedActionType = action.ActionType
                    };
                }
                if (result is UnauthorizedResult)
                {
                    return new GameReplayResult
                    {
                        Success = false,
                        ErrorMessage = $"Action {action.ActionType} ({action.Id}) returned Unauthorized",
                        FailedActionOrderIndex = action.OrderIndex,
                        FailedActionType = action.ActionType
                    };
                }

                var postReplayGame = context.Games.First(g => g.Id == replayGameId);
                _logger.LogDebug($"  -> IsInvestorTurn={postReplayGame.IsInvestorTurn}, Pending={postReplayGame.PendingInvestorIdsJson}");

                if (onActionReplayed != null) await onActionReplayed(action, i, false);
            }

            return new GameReplayResult { Success = true };
        }
        finally
        {
            if (suppressBroadcasts)
            {
                gamesController.SuppressBroadcasts = previousGamesControllerSuppress;
                maneuverController.SuppressBroadcasts = previousManeuverControllerSuppress;
            }
        }
    }
}
