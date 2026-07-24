using Imperial2030.Server.Models;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;

namespace Imperial2030.Server.Services.Bots.Strategies;

public class AggressiveBotStrategy : BotStrategyBase
{
    public override string Name => "Aggressive";

    public override double ScoreRondelSlot(int slot, Game game, NationState ns, Player controller, int factories, int units)
    {
        int unitLimit = factories + 3;
        bool shouldSave = ns.Treasury < 5 && units >= 2;
        return slot switch
        {
            1 => (ns.Treasury >= 5 && CanBuildFactory(game, ns.Nation)) ? 20 : 0,       // Factory
            2 or 6 => (units >= unitLimit || shouldSave) ? 0 : EstimateProductionYield(game, ns.Nation) * 10,  // Higher prod score
            0 => EstimateTaxRevenue(game, ns.Nation) >= 6 ? 20 : (ns.Treasury < 5 ? 18 : 0), 
            3 or 7 => HasExpandableTargets(game, ns.Nation, controller) ? 25 : 0, // Much higher Maneuver score
            5 => (ns.Treasury >= 2 && (units >= unitLimit || shouldSave)) ? 0 : 12,           
            4 => 2,                                                    // Low Investor score
            _ => 0
        };
    }

    public override Bond? ChooseBondToBuy(Game game, Player actor, List<Nation> controlledNations, List<Bond> availableBonds)
    {
        return availableBonds.FirstOrDefault(b => controlledNations.Contains(b.Nation) && b.Cost <= actor.Cash);
    }



    public override double ScoreManeuverDestination(Game game, Unit unit, string destinationId, Player controller)
    {
        var nation = unit.Nation;
        var friendlyNations = game.NationStates
            .Where(ns => ns.ControllerId == controller.Id)
            .Select(ns => ns.Nation)
            .ToList();

        int score = Random.Shared.Next(0, 10);
        bool hasEnemy = game.Units.Any(u => u.TerritoryId == destinationId && !friendlyNations.Contains(u.Nation));
        var ts = game.TerritoryStates.FirstOrDefault(t => t.TerritoryId == destinationId);
        var def = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == destinationId);
        bool isMyHome = def != null && def.Nation == nation;

        if (hasEnemy) 
        {
            if (isMyHome)
            {
                score += 500; // MUST free home
            }
            else
            {
                score += 150; // VERY aggressive towards enemies
            }
        }

        bool uncontrolled = ts == null || ts.Controller == null || !friendlyNations.Contains(ts.Controller.Value);
        if (uncontrolled && !hasEnemy) score += 50; // Less care about empty territories

        return score;
    }

    public override bool RetreatFromBattle(Game game, PendingBattle battle)
    {
        return false; // Aggressive never retreats
    }
}
