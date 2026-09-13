using Imperial2030.Server.Models;
using Imperial2030.Server.Helpers;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;

namespace Imperial2030.Server.Services.Bots.Strategies;

public class DefaultBotStrategy : BotStrategyBase
{
    private const int TaxPowerGainScoreWeight = 4;
    private const int TaxTreasuryGainScoreWeight = 2;
    private const int TaxBonusScoreWeight = 2;

    public override string Name => "Default";

    public override double ScoreRondelSlot(int slot, Game game, NationState ns, Player controller, int factories, int units)
    {
        int unitLimit = factories + 3;
        bool shouldSave = ns.Treasury < 5 && units >= 2;
        return slot switch
        {
            1 => (ns.Treasury >= 5 && CanBuildFactory(game, ns.Nation)) ? 25 : 0,       // Factory
            2 or 6 => (units >= unitLimit || shouldSave) ? 0 : EstimateProductionYield(game, ns.Nation) * 8,   // Production
            0 => ScoreTaxation(game, ns),                                      // Taxation
            3 or 7 => HasExpandableTargets(game, ns.Nation, controller) ? 15 : 0, // Maneuver
            5 => (ns.Treasury >= 2 && (units >= unitLimit || shouldSave)) ? 0 : 10,           // Import
            4 => 3,                                                    // Investor
            _ => 0
        };
    }

    public override bool RetreatFromBattle(Game game, PendingBattle battle)
    {
        return Random.Shared.Next(3) == 0;
    }

    private static double ScoreTaxation(Game game, NationState nationState)
    {
        var preview = TaxationHelper.PreviewTaxation(game, nationState);
        int actualPowerGain = Math.Min(
            preview.ExpectedPowerGain,
            Math.Max(0, GameConstants.MaxPowerPoints - nationState.Power));

        // Score what Taxation will actually deliver. Power is the lasting victory-value gain, while
        // treasury and the controller's cash bonus are both immediately spendable resources.
        return actualPowerGain * TaxPowerGainScoreWeight
            + preview.ExpectedTreasuryGain * TaxTreasuryGainScoreWeight
            + preview.ExpectedBonus * TaxBonusScoreWeight;
    }
}
