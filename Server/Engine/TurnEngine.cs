using Imperial2030.Server.Data;
using Imperial2030.Server.Helpers;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Imperial2030.Server.Engine;

/// <summary>
/// Ending the acting nation's turn. Imperial-2030-Rules.pdf p.7: "after that, the nations move clockwise"
/// - the rotation itself is <see cref="Game.AdvanceTurn"/>; this is the operation that invokes it once
/// the nation's action is complete, and logs that it did.
/// </summary>
public static class TurnEngine
{
    /// <summary>
    /// Ends <see cref="Game.CurrentTurnNation"/>'s turn: refuses while an Investor phase, a battle or an
    /// unfinished maneuver phase is still open, otherwise advances the rotation and logs <c>EndTurn</c>
    /// for the nation that just finished.
    ///
    /// Three callers used to do this themselves. The endpoint had the guards and the log; the bot had
    /// the log but only the maneuver guard; the training server advanced with neither guard nor log,
    /// which is why exported training games show a nation's Factory or Import turn followed straight by
    /// the next nation's Move with no <c>EndTurn</c> between (implementation_plan.md divergence #8).
    /// </summary>
    public static EngineResult EndTurn(ApplicationDbContext? context, Game game)
    {
        if (game.Status != GameStatus.InProgress) return EngineResult.Fail("Game not in progress.");
        if (game.IsInvestorTurn) return EngineResult.Fail("Waiting for Investor Phase.");
        if (game.PendingBattleDefenders.Any()) return EngineResult.Fail("Cannot end turn while a battle is pending.");
        if (game.CurrentManeuverPhase != ManeuverPhase.None) return EngineResult.Fail($"Finish your maneuver phase ({game.CurrentManeuverPhase}) first.");

        var nation = game.CurrentTurnNation;
        var nationState = game.NationStates.First(n => n.Nation == nation);

        // Controller Check
        if (nationState.ControllerId == null) return EngineResult.Fail("No controller for this nation.");
        var controller = game.Players.First(p => p.Id == nationState.ControllerId);

        // Advance Turn (Russia -> China -> India -> Brazil -> USA -> Europe)
        // Note: game.AdvanceTurn() handles all state flag resetting!
        game.AdvanceTurn();

        if (context != null) context.Entry(game).State = EntityState.Modified;

        GameLogger.LogEndTurn(context, game, nation, controller.GetPlayerName(context));
        return EngineResult.Success;
    }
}
