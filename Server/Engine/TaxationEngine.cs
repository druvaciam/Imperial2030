using Imperial2030.Server.Data;
using Imperial2030.Server.Helpers;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Imperial2030.Server.Engine;

/// <summary>
/// Outcome of a Taxation turn: the figures the endpoint returns to the client and the log records, plus
/// whether this taxation took the nation to <see cref="GameConstants.MaxPowerPoints"/> and ended the game.
/// </summary>
public sealed record TaxationOutcome(
    bool Ok,
    string? Error = null,
    int TotalTaxRevenue = 0,
    int SoldiersPay = 0,
    int Bonus = 0,
    int PowerGain = 0,
    bool GameEnded = false) : EngineResult(Ok, Error)
{
    public static new TaxationOutcome Fail(string error) => new(false, error);
}

/// <summary>
/// The Taxation rondel action. The arithmetic - revenue from factories and flags, soldiers' pay, the
/// government's bonus and the power gain - is <see cref="TaxationHelper"/>'s and is not repeated here;
/// this is the turn around it: the checks, the log, the game-end test (p.12: the game ends when a
/// nation reaches 25 power) and, otherwise, the turn advance. Taxation ends the turn by itself, without
/// an <c>EndTurn</c> entry - both the endpoint and the bot have always done it that way, and replay
/// relies on it.
/// </summary>
public static class TaxationEngine
{
    /// <summary>
    /// Taxes for <see cref="Game.CurrentTurnNation"/>. On <see cref="TaxationOutcome.GameEnded"/> the game
    /// is marked <see cref="GameStatus.Finished"/> with <see cref="Game.FinishedAt"/> set, and the caller
    /// resolves the winner's name (<see cref="GameHelper.SetWinnerNameAsync"/> needs the context) and
    /// announces it. Otherwise the turn has advanced. Does not save or broadcast.
    /// </summary>
    public static TaxationOutcome ExecuteTaxation(ApplicationDbContext? context, Game game)
    {
        if (game.Status != GameStatus.InProgress) return TaxationOutcome.Fail("Game not in progress.");
        if (game.IsInvestorTurn) return TaxationOutcome.Fail("Waiting for Investor Phase.");

        var nation = game.CurrentTurnNation;
        var nationState = game.NationStates.First(n => n.Nation == nation);

        // Controller Check
        if (nationState.ControllerId == null) return TaxationOutcome.Fail("No controller for this nation.");
        var controller = game.Players.First(p => p.Id == nationState.ControllerId);

        // Validate Rondel Position: Must be on Taxation
        if (nationState.RondelPosition != RondelData.TaxationSlot) return TaxationOutcome.Fail("Nation must be on 'Taxation' slot.");

        int oldTreasury = nationState.Treasury;

        // --- Apply Centralized Taxation Logic ---
        var result = TaxationHelper.ApplyTaxation(game, nationState, controller);

        if (context != null)
        {
            // Mark Controller as modified if they gained cash
            if (result.Bonus > 0) context.Entry(controller).State = EntityState.Modified;
            context.Entry(nationState).State = EntityState.Modified;
        }

        int treasuryGain = nationState.Treasury - oldTreasury;
        GameLogger.LogTaxation(context, game, result.TotalTaxRevenue, result.SoldiersPay, treasuryGain, result.Bonus, result.PowerGain, nation, controller.GetPlayerName(context));

        // --- Game End Check ---
        bool gameEnded = nationState.Power >= GameConstants.MaxPowerPoints;
        if (gameEnded)
        {
            game.Status = GameStatus.Finished;
            game.FinishedAt = DateTime.UtcNow;
        }
        else
        {
            // Taxation auto-advances the turn (resets all turn state flags automatically)
            game.AdvanceTurn();
        }
        if (context != null) context.Entry(game).State = EntityState.Modified;

        return new TaxationOutcome(true,
            TotalTaxRevenue: result.TotalTaxRevenue,
            SoldiersPay: result.SoldiersPay,
            Bonus: result.Bonus,
            PowerGain: result.PowerGain,
            GameEnded: gameEnded);
    }
}
