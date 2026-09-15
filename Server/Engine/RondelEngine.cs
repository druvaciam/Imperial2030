using Imperial2030.Server.Data;
using Imperial2030.Server.Helpers;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Imperial2030.Server.Engine;

/// <summary>
/// The rondel move. Imperial-2030-Rules.pdf p.6: the marker moves clockwise, staying put is not allowed,
/// the first three spaces are free and each further space costs the government's player money; p.11:
/// passing the Investor space activates the investor, landing on it also pays interest; p.12: a Swiss
/// Bank may force a stop on Investor when the nation can pay its interest.
/// </summary>
public static class RondelEngine
{
    /// <summary>
    /// Validates and executes <paramref name="nation"/>'s move to <paramref name="targetSlot"/>.
    ///
    /// On a forced stop by a Swiss Bank the result is <see cref="RondelMoveResult.SwissBankForcedStop"/> and
    /// NOTHING has moved: the pending-force state is set on the game and the caller must stop and wait
    /// for the responders. Otherwise the move is applied, logged, and any Investor slot crossed or landed
    /// on has already been handled by <see cref="InvestorEngine.HandleInvestorPhase"/> - so the caller
    /// should check <see cref="Game.IsInvestorTurn"/> before continuing with the slot's action.
    ///
    /// Does not save, broadcast or trigger anything. The acting player is the nation's government, found
    /// from <see cref="NationState.ControllerId"/>; authorising that the HTTP caller IS that player is
    /// the controller's job, before calling this.
    /// </summary>
    public static RondelMoveResult MoveNation(ApplicationDbContext? context, Game game, Nation nation, int targetSlot)
    {
        if (game.Status != GameStatus.InProgress) return RondelMoveResult.Fail("Game not in progress.");
        if (game.IsInvestorTurn) return RondelMoveResult.Fail("Waiting for Investor Phase.");
        if (game.CurrentTurnNation != nation) return RondelMoveResult.Fail($"It is {game.CurrentTurnNation}'s turn.");
        if (targetSlot < 0 || targetSlot >= RondelData.SlotCount) return RondelMoveResult.Fail($"Invalid slot {targetSlot}. Must be 0-{RondelData.SlotCount - 1}.");

        var nationState = game.NationStates.First(n => n.Nation == nation);

        // Controller Check
        if (nationState.ControllerId == null) return RondelMoveResult.Fail("No controller for this nation.");

        var controller = game.Players.First(p => p.Id == nationState.ControllerId);

        // Check if already moved
        if (nationState.HasMovedThisTurn) return RondelMoveResult.Fail("Already moved this turn.");

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
            if (currentSlot.Value == targetSlot) return RondelMoveResult.Fail("Must move to a different slot.");

            int distance = (targetSlot - currentSlot.Value + RondelData.SlotCount) % RondelData.SlotCount;

            if (distance == 0) return RondelMoveResult.Fail("Must move at least 1 step."); // Should be covered by above equality check but safe.
            if (distance > RondelData.MaxMoveDistance) return RondelMoveResult.Fail($"Cannot move more than {RondelData.MaxMoveDistance} spaces on the rondel.");
            cost = RondelData.GetMoveCost(currentSlot, targetSlot, nationState.Power);
        }

        if (cost > 0 && controller.Cash < cost) return RondelMoveResult.Fail($"Not enough cash. Cost: {cost}M");

        // --- Swiss Bank Forced-Stop Logic ---
        bool crossingInvestor = false;
        if (currentSlot != null && targetSlot != RondelData.InvestorSlot)
        {
            int dist = (targetSlot - currentSlot.Value + RondelData.SlotCount) % RondelData.SlotCount;
            for (int i = 1; i < dist; i++) // Check intermediate steps
            {
                if ((currentSlot.Value + i) % RondelData.SlotCount == RondelData.InvestorSlot)
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
                var swissBankPlayers = game.Players.Where(p => !game.NationStates.Any(ns => ns.ControllerId == p.Id)).GetOrderedPlayers().ToList();
                if (swissBankPlayers.Any())
                {
                    game.PendingSwissBankForceNation = nation;
                    game.PendingSwissBankForceTargetSlot = targetSlot;
                    game.PendingSwissBankResponders = swissBankPlayers.Select(p => p.Id).ToList();
                    return RondelMoveResult.ForcedStop();
                }
            }
        }
        // --- End Swiss Bank Forced-Stop Logic ---

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

        // Marks the nation as moved, clears its once-per-turn action flags and resets its units' movement.
        // Turn advancement is manual, via EndTurn.
        game.ResetStateForNewMove(nationState, u => { if (context != null) context.Entry(u).State = EntityState.Modified; });
        if (context != null)
        {
            context.Entry(controller).State = EntityState.Modified;
            context.Entry(nationState).State = EntityState.Modified;
        }

        // GetPlayerName, not User.Identity?.Name: replay's auth context carries only NameIdentifier.
        var controllerName = controller.GetPlayerName(context);
        GameLogger.LogRondelMove(context, game, targetSlot, currentSlot, cost, nation, controllerName);

        // Check for Investor Slot (Index 4)
        bool triggeredInvestor = false;
        if (currentSlot != null)
        {
            // Moving from currentSlot to targetSlot (clockwise)
            // Path: (current + 1) ... targetSlot
            int dist = (targetSlot - currentSlot.Value + RondelData.SlotCount) % RondelData.SlotCount;
            for (int i = 1; i <= dist; i++)
            {
                int step = (currentSlot.Value + i) % RondelData.SlotCount;
                if (step == RondelData.InvestorSlot)
                {
                    triggeredInvestor = true;
                    break;
                }
            }
        }
        else
        {
            // First placement: if placed on Investor
            if (targetSlot == RondelData.InvestorSlot) triggeredInvestor = true;
        }

        if (triggeredInvestor)
        {
            // Calculate if landed on
            // Note: The loop logic above is slightly flawed if we just check targetSlot==Investor for "landedOn"
            // because distinct "pass through" vs "land on" matters for 2M bonus.
            // But for now, sticking to existing logic structure.
            bool landedOn = (targetSlot == RondelData.InvestorSlot);
            InvestorEngine.HandleInvestorPhase(context, game, nationState, controller, landedOn);
        }

        // Initialize the phase for the slot the move actually reached.
        game.InitializeRondelActionPhase(targetSlot);

        return RondelMoveResult.Moved(currentSlot, cost);
    }

    /// <summary>
    /// After landing on a Maneuver slot: skip the Fleets phase if the nation has no unmoved fleet, then
    /// the Armies phase if it has no unmoved army, logging each skip.
    ///
    /// Separate from <see cref="MoveNation"/> because only the HTTP path calls it: <c>BotManeuver</c>
    /// walks both phases itself and logs <c>AutoEndPhase</c> for each, so calling this for a bot as well
    /// would double-log the phase end.
    /// </summary>
    public static void AutoSkipEmptyManeuverPhases(ApplicationDbContext? context, Game game, Nation nation, string playerName)
    {
        if (game.CurrentManeuverPhase == ManeuverPhase.Fleets)
        {
            bool hasFleets = game.Units.Any(u => u.Nation == nation && u.UnitType == UnitType.Fleet && !u.HasMoved);
            if (!hasFleets)
            {
                game.CurrentManeuverPhase = ManeuverPhase.Armies;
                GameLogger.LogAutoSkipManeuverPhase(context, game, "Fleets", nation, playerName);
            }
            if (game.CurrentManeuverPhase == ManeuverPhase.Armies)
            {
                bool hasArmies = game.Units.Any(u => u.Nation == nation && u.UnitType == UnitType.Army && !u.HasMoved);
                if (!hasArmies)
                {
                    game.CurrentManeuverPhase = ManeuverPhase.None;
                    GameLogger.LogAutoSkipManeuverPhase(context, game, "Armies", nation, playerName);
                }
            }
        }
    }
}
