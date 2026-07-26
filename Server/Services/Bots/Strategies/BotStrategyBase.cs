using Imperial2030.Server.Models;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;

namespace Imperial2030.Server.Services.Bots.Strategies;

public abstract class BotStrategyBase : IBotStrategy
{
    public abstract string Name { get; }

    public abstract double ScoreRondelSlot(int slot, Game game, NationState ns, Player controller, int factories, int units);
    public virtual Bond? ChooseBondToBuy(Game game, Player actor, List<Nation> controlledNations, List<Bond> availableBonds) => null;
    
    public virtual string? ChooseCityForFactory(Game game, Nation nation, List<Territory> validCities)
    {
        if (validCities.Count == 0) return null;
        var rng = new Random();
        return validCities[rng.Next(validCities.Count)].Id;
    }
    
    public virtual List<(UnitType Type, string TerritoryId)> ChooseImports(Game game, NationState ns, int maxImport, List<Territory> homeTerritories)
    {
        var result = new List<(UnitType Type, string TerritoryId)>();
        var nation = ns.Nation;
        int imported = 0;
        int currentArmies = game.Units.Count(u => u.Nation == nation && u.UnitType == UnitType.Army);
        int currentFleets = game.Units.Count(u => u.Nation == nation && u.UnitType == UnitType.Fleet);

        var validTerritories = homeTerritories.Where(t => 
            !game.Units.Any(u => u.TerritoryId == t.Id && u.Nation != nation && u.UnitType == UnitType.Army && u.IsHostile)
        ).ToList();

        if (validTerritories.Count == 0) return result;

        while (imported < maxImport)
        {
            bool built = false;
            foreach (var t in validTerritories)
            {
                if (imported >= maxImport) break;

                bool canBuildArmy = currentArmies < NationData.GetMaxArmies(nation);
                bool canBuildFleet = currentFleets < NationData.GetMaxFleets(nation) && t.CityType == CityType.LightBlue;

                if (!canBuildArmy && !canBuildFleet) continue;

                UnitType typeToBuild = UnitType.Army;
                if (canBuildFleet && (!canBuildArmy || imported % 2 == 0))
                {
                    typeToBuild = UnitType.Fleet;
                }
                else if (!canBuildArmy)
                {
                    typeToBuild = UnitType.Fleet;
                }
                
                result.Add((typeToBuild, t.Id));
                if (typeToBuild == UnitType.Army) currentArmies++;
                if (typeToBuild == UnitType.Fleet) currentFleets++;
                imported++;
                built = true;
            }
            
            if (!built) break;
        }

        return result;
    }
    public abstract double ScoreManeuverDestination(Game game, Unit unit, string destinationId, Player controller);
    public abstract bool RetreatFromBattle(Game game, PendingBattle battle);

    // Shared helpers
    protected int EstimateProductionYield(Game game, Nation nation)
    {
        int produced = 0;
        int currentArmies = game.Units.Count(u => u.Nation == nation && u.UnitType == UnitType.Army);
        int currentFleets = game.Units.Count(u => u.Nation == nation && u.UnitType == UnitType.Fleet);

        foreach (var ts in game.TerritoryStates.Where(t => t.HasFactory))
        {
            var def = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == ts.TerritoryId);
            if (def?.Nation != nation) continue;
            bool blocked = game.Units.Any(u => u.TerritoryId == ts.TerritoryId && u.UnitType == UnitType.Army && u.Nation != nation && u.IsHostile);
            if (blocked) continue;

            var unitType = def.CityType == CityType.LightBlue ? UnitType.Fleet : UnitType.Army;
            if (unitType == UnitType.Army)
            {
                if (currentArmies >= NationData.GetMaxArmies(nation)) continue;
                currentArmies++;
            }
            else
            {
                if (currentFleets >= NationData.GetMaxFleets(nation)) continue;
                currentFleets++;
            }
            produced++;
        }
        return produced;
    }

    protected int EstimateTaxRevenue(Game game, Nation nation)
    {
        int rev = 0;
        foreach (var ts in game.TerritoryStates.Where(t => t.HasFactory))
        {
            var def = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == ts.TerritoryId);
            if (def?.Nation == nation)
            {
                bool blocked = game.Units.Any(u => u.TerritoryId == ts.TerritoryId && u.UnitType == UnitType.Army && u.Nation != nation && u.IsHostile);
                if (!blocked) rev += 2;
            }
        }
        rev += game.TerritoryStates.Count(ts => ts.Controller == nation);
        return Math.Min(23, rev);
    }

    protected bool HasExpandableTargets(Game game, Nation nation, Player controller)
    {
        var friendlyNations = game.NationStates
            .Where(ns => ns.ControllerId == controller.Id)
            .Select(ns => ns.Nation)
            .ToList();

        var myArmyTerritories = game.Units.Where(u => u.Nation == nation && u.UnitType == UnitType.Army).Select(u => u.TerritoryId).Distinct();
        foreach (var tid in myArmyTerritories)
        {
            if (MapConnectivity.Adjacency.TryGetValue(tid, out var neighbors))
            {
                if (neighbors.Any(n => 
                {
                    var ts = game.TerritoryStates.FirstOrDefault(t => t.TerritoryId == n);
                    var hasEnemy = game.Units.Any(u => u.TerritoryId == n && !friendlyNations.Contains(u.Nation));
                    var isUncontrolled = ts == null || ts.Controller == null || !friendlyNations.Contains(ts.Controller.Value);
                    return isUncontrolled || hasEnemy;
                }))
                {
                    return true;
                }
            }
        }
        
        var myFleetTerritories = game.Units.Where(u => u.Nation == nation && u.UnitType == UnitType.Fleet).Select(u => u.TerritoryId).Distinct();
        foreach (var tid in myFleetTerritories)
        {
            if (MapConnectivity.Adjacency.TryGetValue(tid, out var neighbors))
            {
                if (neighbors.Any(n => 
                {
                    var def = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == n);
                    return def != null && def.Type == TerritoryType.Sea && !game.Units.Any(u => u.TerritoryId == n && u.UnitType == UnitType.Fleet && friendlyNations.Contains(u.Nation));
                }))
                {
                    return true;
                }
            }
        }

        return false;
    }

    protected bool CanBuildFactory(Game game, Nation nation)
    {
        var homeCities = TerritoryData.AllTerritories.Where(t => t.Nation == nation && t.CityType != CityType.None);
        foreach (var city in homeCities)
        {
            var ts = game.TerritoryStates.FirstOrDefault(t => t.TerritoryId == city.Id);
            if (ts == null || !ts.HasFactory)
            {
                bool hasHostileForeignArmy = game.Units.Any(u => u.TerritoryId == city.Id && u.UnitType == UnitType.Army && u.Nation != nation && u.IsHostile);
                if (!hasHostileForeignArmy) return true;
            }
        }
        return false;
    }
}
