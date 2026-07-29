using Imperial2030.Server.Models;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;

namespace Imperial2030.Server.Services.Bots.Strategies;

public class DefaultBotStrategy : BotStrategyBase
{
    public override string Name => "Default";

    public override double ScoreRondelSlot(int slot, Game game, NationState ns, Player controller, int factories, int units)
    {
        int unitLimit = factories + 3;
        bool shouldSave = ns.Treasury < 5 && units >= 2;
        return slot switch
        {
            1 => (ns.Treasury >= 5 && CanBuildFactory(game, ns.Nation)) ? 25 : 0,       // Factory
            2 or 6 => (units >= unitLimit || shouldSave) ? 0 : EstimateProductionYield(game, ns.Nation) * 8,   // Production
            0 => EstimateTaxRevenue(game, ns.Nation) >= 6 ? 22 : (ns.Treasury < 5 ? 18 : 0), // Taxation
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
}
