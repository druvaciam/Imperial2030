using Imperial2030.Server.Data;
using Imperial2030.Server.Helpers;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Imperial2030.Server.Engine;

/// <summary>
/// Ending the acting nation's turn. Imperial-2030-Rules.pdf p.7: "after that, the nations move clockwise"
/// - the rotation itself is <see cref="Game.AdvanceTurn"/>; this is the operation that invokes it once
/// the nation's action is complete, and logs that it did. When the move passed over Investor, the
/// Investor turn comes between the two (p.11: "the action determined by the space landed on is
/// completed first"): <see cref="ActionComplete"/> opens it, and <see cref="InvestorEngine"/> calls
/// <see cref="MoveOn"/> when it ends.
/// </summary>
public static class TurnEngine
{
    /// <summary>
    /// Ends <see cref="Game.CurrentTurnNation"/>'s turn: refuses while an Investor turn, a battle, a Swiss
    /// Bank question or an unfinished maneuver phase is still open. Otherwise the action is complete: a
    /// passed-over Investor turn opens now and the rotation moves on when it ends; with none owed the
    /// rotation moves on at once, logging <c>EndTurn</c> for the nation that just finished.
    /// </summary>
    public static EngineResult EndTurn(ApplicationDbContext? context, Game game)
    {
        if (game.Status != GameStatus.InProgress) return EngineResult.Fail("Game not in progress.");
        if (game.IsInvestorTurn) return EngineResult.Fail("Waiting for Investor Phase.");
        if (game.PendingBattleDefenders.Any()) return EngineResult.Fail("Cannot end turn while a battle is pending.");
        // The nation's rondel move is still being decided by the Swiss Banks (p.12) - it has not happened yet.
        if (game.PendingSwissBankForceNation != null) return EngineResult.Fail("Cannot end turn while a Swiss Bank decision is pending.");
        if (game.CurrentManeuverPhase != ManeuverPhase.None) return EngineResult.Fail($"Finish your maneuver phase ({game.CurrentManeuverPhase}) first.");

        var nationState = game.NationStates.First(n => n.Nation == game.CurrentTurnNation);

        // Controller Check
        if (nationState.ControllerId == null) return EngineResult.Fail("No controller for this nation.");

        ActionComplete(context, game);
        return EngineResult.Success;
    }

    /// <summary>
    /// The nation's action is complete. If its move passed over Investor, the Investor turn opens now
    /// (p.11) and the rotation waits for it; otherwise - or if there is nobody to activate - the rotation
    /// moves on at once.
    /// </summary>
    public static void ActionComplete(ApplicationDbContext? context, Game game)
    {
        if (game.InvestorTurnPending)
        {
            var nationState = game.NationStates.First(n => n.Nation == game.CurrentTurnNation);
            var controller = game.Players.First(p => p.Id == nationState.ControllerId);
            InvestorEngine.HandleInvestorPhase(context, game, nationState, controller, isLandedOn: false);
            if (game.IsInvestorTurn)
            {
                if (context != null) context.Entry(game).State = EntityState.Modified;
                return; // InvestorEngine calls MoveOn when the last investor is done
            }
        }
        MoveOn(context, game);
    }

    /// <summary>
    /// Advances the rotation past <see cref="Game.CurrentTurnNation"/> and logs <c>EndTurn</c> for it.
    /// </summary>
    public static void MoveOn(ApplicationDbContext? context, Game game)
    {
        var nation = game.CurrentTurnNation;
        var nationState = game.NationStates.First(n => n.Nation == nation);
        var controller = game.Players.First(p => p.Id == nationState.ControllerId);

        game.InvestorTurnPending = false;
        // Advance Turn (Russia -> China -> India -> Brazil -> USA -> Europe)
        // Note: game.AdvanceTurn() handles all state flag resetting!
        game.AdvanceTurn();

        if (context != null) context.Entry(game).State = EntityState.Modified;

        GameLogger.LogEndTurn(context, game, nation, controller.GetPlayerName(context));
    }
}
