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

    public override Bond? ChooseBondToBuy(Game game, Player actor, List<Nation> controlledNations, List<Bond> availableBonds)
    {
        var affordableBonds = availableBonds.Where(b => b.Cost <= actor.Cash).ToList();
        
        // 1. Try to strengthen control of own nations first
        var ownBond = affordableBonds.FirstOrDefault(b => controlledNations.Contains(b.Nation));
        if (ownBond != null) return ownBond;

        // 2. Buy bonds in the strongest nations first, then by highest yield (cost)
        return affordableBonds
            .OrderByDescending(b => game.NationStates.First(n => n.Nation == b.Nation).Power)
            .ThenByDescending(b => b.Cost)
            .FirstOrDefault();
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
                score += 200; // High priority to free own home territory
                if (ts != null && ts.HasFactory) 
                {
                    score += 300; // Even higher priority to free factories
                }
            }
            else
            {
                score += 10; // Normal enemy
            }
        }

        bool hasEnemyFactory = ts != null && ts.HasFactory && def != null && def.Nation.HasValue && !friendlyNations.Contains(def.Nation.Value);
        if (hasEnemyFactory)
        {
            var targetNation = def.Nation.Value;
            int investment = game.Bonds.Where(b => b.HolderId == controller.Id && b.Nation == targetNation).Sum(b => b.Cost);
            if (investment <= 2 && Random.Shared.Next(0, 10) < 5) // 50% chance to block if investment is low
            {
                score += 150;
            }
        }

        bool isHomeProvince = def != null && def.Nation.HasValue;
        bool uncontrolled = !isHomeProvince && (ts == null || ts.Controller == null || !friendlyNations.Contains(ts.Controller.Value));
        if (uncontrolled && !hasEnemy) score += 100;

        bool notFriendlyHome = def?.Nation == null || !friendlyNations.Contains(def.Nation.Value);
        if (notFriendlyHome) score += 10;
        else if (!hasEnemy) score -= 50; // Penalize moving within friendly home territories if there is no enemy

        return score;
    }

    public override bool RetreatFromBattle(Game game, PendingBattle battle)
    {
        // Default bot never retreats
        return false;
    }
}
