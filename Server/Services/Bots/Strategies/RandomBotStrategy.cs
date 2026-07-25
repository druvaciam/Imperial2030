using Imperial2030.Server.Models;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;

namespace Imperial2030.Server.Services.Bots.Strategies;

public class RandomBotStrategy : BotStrategyBase
{
    public override string Name => "Random";

    public override double ScoreRondelSlot(int slot, Game game, NationState ns, Player controller, int factories, int units)
    {
        // Add random score between 0 and 10 to any valid move.
        // Keep some heuristics to ensure it doesn't make completely invalid moves (like factory when it has no money)
        int unitLimit = factories + 3;
        bool shouldSave = ns.Treasury < 5 && units >= 2;
        double baseScore = slot switch
        {
            1 => (ns.Treasury >= 5 && CanBuildFactory(game, ns.Nation)) ? 1 : 0,
            2 or 6 => (units >= unitLimit || shouldSave) ? 0 : 1,
            0 => EstimateTaxRevenue(game, ns.Nation) >= 6 ? 1 : (ns.Treasury < 5 ? 1 : 0),
            3 or 7 => HasExpandableTargets(game, ns.Nation, controller) ? 1 : 0,
            5 => (ns.Treasury >= 2 && (units >= unitLimit || shouldSave)) ? 0 : 1,
            4 => 1,
            _ => 0
        };
        
        return baseScore > 0 ? Random.Shared.NextDouble() * 10 : 0;
    }

    public override Bond? ChooseBondToBuy(Game game, Player actor, List<Nation> controlledNations, List<Bond> availableBonds)
    {
        var affordableBonds = availableBonds.Where(b => b.Cost <= actor.Cash).ToList();
        if (!affordableBonds.Any()) return null;
        return affordableBonds[Random.Shared.Next(affordableBonds.Count)];
    }

    public override string? ChooseCityForFactory(Game game, Nation nation, List<Territory> validCities)
    {
        if (!validCities.Any()) return null;
        return validCities[Random.Shared.Next(validCities.Count)].Id;
    }

    public override List<(UnitType Type, string TerritoryId)> ChooseImports(Game game, NationState ns, int maxImport, List<Territory> homeTerritories)
    {
        return base.ChooseImports(game, ns, maxImport, homeTerritories); // Re-use base for simplicity of valid rules
    }

    public override double ScoreManeuverDestination(Game game, Unit unit, string destinationId, Player controller)
    {
        return Random.Shared.NextDouble() * 10;
    }

    public override bool RetreatFromBattle(Game game, PendingBattle battle)
    {
        return Random.Shared.Next(2) == 0;
    }
}
