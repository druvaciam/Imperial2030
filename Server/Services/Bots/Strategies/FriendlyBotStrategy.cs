using Imperial2030.Server.Models;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;

namespace Imperial2030.Server.Services.Bots.Strategies;

public class FriendlyBotStrategy : BotStrategyBase
{
    public override string Name => "Friendly";

    public override bool DetermineHostility(bool hasEnemy, bool isForeignHome)
    {
        if (!hasEnemy && !isForeignHome) return false;
        return Random.Shared.Next(0, 100) < 10; // 10% hostile
    }

    public override double ScoreRondelSlot(int slot, Game game, NationState ns, Player controller, int factories, int units)
    {
        int unitLimit = factories + 3;
        bool shouldSave = ns.Treasury < 5 && units >= 2;
        return slot switch
        {
            1 => (ns.Treasury >= 5 && CanBuildFactory(game, ns.Nation)) ? 25 : 0,       // Factory
            2 or 6 => (units >= unitLimit || shouldSave) ? 0 : EstimateProductionYield(game, ns.Nation) * 8,
            0 => EstimateTaxRevenue(game, ns.Nation) >= 6 ? 22 : (ns.Treasury < 5 ? 18 : 0),
            3 or 7 => HasExpandableTargets(game, ns.Nation, controller) ? 12 : 0, // Lower maneuver priority
            5 => (ns.Treasury >= 2 && (units >= unitLimit || shouldSave)) ? 0 : 10,
            4 => 5,                                                    // Higher investor score
            _ => 0
        };
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
                score += 150; // Will free home, but less aggressive
            }
            else
            {
                score -= 100; // Penalize attacking
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
        return true; // Friendly bot retreats if possible
    }

    public override bool ShouldDestroyFactory(Game game, Nation nation, string territoryId, Player controller)
    {
        return false; // Friendly never destroys factories
    }
}
