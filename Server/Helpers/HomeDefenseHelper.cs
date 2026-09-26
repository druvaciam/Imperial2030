using Imperial2030.Server.Models;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;

namespace Imperial2030.Server.Helpers;

public sealed record HomeProvinceDefense(
    Territory Territory,
    int ThreateningArmyOccupiers,
    int ReachableEnemyArmies,
    int FriendlyArmyDefenders,
    int AdjacentEnemyFleets)
{
    public int LandThreat => ThreateningArmyOccupiers + ReachableEnemyArmies;
    public int LandDefenseDeficit => Math.Max(0, LandThreat - FriendlyArmyDefenders);
    public bool IsOccupied => ThreateningArmyOccupiers > 0;
}

/// <summary>
/// One shared view of homeland danger for Default movement, rondel, and Import decisions.
/// It evaluates the current board only; it does not change game state or relax placement rules.
/// </summary>
public static class HomeDefenseHelper
{
    public static IReadOnlyList<HomeProvinceDefense> Assess(Game game, Nation nation, Guid? controllerId)
    {
        var friendlyNations = game.NationStates
            .Where(state => controllerId.HasValue && state.ControllerId == controllerId)
            .Select(state => state.Nation)
            .Append(nation)
            .ToHashSet();
        var homeTerritories = TerritoryData.AllTerritories
            .Where(territory => territory.Nation == nation)
            .ToList();
        var homeIds = homeTerritories.Select(territory => territory.Id).ToHashSet();
        var threateningArmyOccupiers = homeIds.ToDictionary(id => id, _ => 0);
        var reachableEnemyArmies = homeIds.ToDictionary(id => id, _ => 0);
        var friendlyArmyDefenders = homeIds.ToDictionary(id => id, _ => 0);
        var adjacentEnemyFleets = homeIds.ToDictionary(id => id, _ => 0);

        foreach (var unit in game.Units)
        {
            bool friendly = friendlyNations.Contains(unit.Nation);
            if (unit.UnitType == UnitType.Army && friendly && homeIds.Contains(unit.TerritoryId))
            {
                friendlyArmyDefenders[unit.TerritoryId]++;
                continue;
            }

            if (unit.UnitType == UnitType.Army && !friendly)
            {
                // A foreign army already inside an ordinary home province can fight on its next
                // Maneuver even while currently lying down (p.10 and FAQ p.14). The protected last
                // unoccupied factory is the exception: it cannot be occupied hostilely or destroyed,
                // so this strategic defense model assigns it no garrison demand.
                if (homeIds.Contains(unit.TerritoryId)
                    && !ManeuverHelper.IsProtectedLastFactoryProvince(
                        game,
                        unit.Nation,
                        unit.TerritoryId))
                {
                    threateningArmyOccupiers[unit.TerritoryId]++;
                }

                var reachableHomes = ManeuverHelper.GetAllReachableArmyDestinations(
                        game,
                        unit.TerritoryId,
                        unit.Nation)
                    .Select(destination => destination.TerritoryId)
                    .Where(homeIds.Contains)
                    .Where(homeId => !ManeuverHelper.IsProtectedLastFactoryProvince(
                        game,
                        unit.Nation,
                        homeId))
                    .Distinct();
                foreach (string homeId in reachableHomes)
                {
                    reachableEnemyArmies[homeId]++;
                }
                continue;
            }

            if (unit.UnitType == UnitType.Fleet && !friendly)
            {
                foreach (var port in homeTerritories.Where(territory => territory.CityType == CityType.LightBlue))
                {
                    if (MapConnectivity.GetNeighbors(port.Id, isFleet: true).Contains(unit.TerritoryId))
                    {
                        adjacentEnemyFleets[port.Id]++;
                    }
                }
            }
        }

        return homeTerritories.Select(territory => new HomeProvinceDefense(
            territory,
            threateningArmyOccupiers[territory.Id],
            reachableEnemyArmies[territory.Id],
            friendlyArmyDefenders[territory.Id],
            adjacentEnemyFleets[territory.Id])).ToList();
    }

    public static bool CanArmyReach(Game game, Nation nation, string originId, string destinationId)
    {
        return string.Equals(originId, destinationId, StringComparison.OrdinalIgnoreCase)
            || ManeuverHelper.GetAllReachableArmyDestinations(game, originId, nation)
                .Any(destination => string.Equals(
                    destination.TerritoryId,
                    destinationId,
                    StringComparison.OrdinalIgnoreCase));
    }

}
