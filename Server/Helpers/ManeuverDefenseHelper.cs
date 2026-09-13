using Imperial2030.Server.Models;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;

namespace Imperial2030.Server.Helpers;

/// <summary>
/// Shared maneuver evaluation used by both heuristic bots and RL training rewards.
/// </summary>
public static class ManeuverDefenseHelper
{
    /// <summary>
    /// True when moving <paramref name="unit"/> to <paramref name="targetTerritoryId"/> adds an army to
    /// a neutral region that friendly armies already defend strongly enough against every enemy army
    /// able to reach it on its next maneuver.
    /// </summary>
    public static bool IsRedundantStackMove(
        Game game,
        Unit unit,
        string targetTerritoryId,
        ISet<Nation>? friendlyNations = null)
    {
        if (unit.UnitType != UnitType.Army) return false;
        if (targetTerritoryId == unit.TerritoryId) return false;

        var target = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == targetTerritoryId);

        // Neutral land only: home provinces (anyone's) have their own reasons to be stacked.
        if (target == null || target.Nation.HasValue || target.Type != TerritoryType.Land) return false;

        bool IsFriendly(Nation nation) => friendlyNations?.Contains(nation) ?? nation == unit.Nation;

        var occupants = game.Units
            .Where(candidate => candidate.TerritoryId == targetTerritoryId && candidate.Id != unit.Id)
            .ToList();

        // Anything foreign there makes this reinforcement, not waste.
        if (occupants.Any(candidate => !IsFriendly(candidate.Nation))) return false;

        int defendersAlreadyThere = occupants.Count(candidate => candidate.UnitType == UnitType.Army);
        if (defendersAlreadyThere == 0) return false;

        return defendersAlreadyThere >= EnemyArmiesAbleToReach(
            game,
            targetTerritoryId,
            IsFriendly,
            unit.Id);
    }

    /// <summary>
    /// Counts enemy armies that could enter <paramref name="territoryId"/> on their next maneuver using
    /// the engine's normal adjacency, rail and convoy reachability.
    /// </summary>
    private static int EnemyArmiesAbleToReach(
        Game game,
        string territoryId,
        Func<Nation, bool> isFriendly,
        Guid excludeUnitId)
    {
        return game.Units.Count(candidate =>
            candidate.Id != excludeUnitId &&
            candidate.UnitType == UnitType.Army &&
            !isFriendly(candidate.Nation) &&
            ManeuverHelper.GetAllReachableArmyDestinations(
                    game,
                    candidate.TerritoryId,
                    candidate.Nation)
                .Any(destination => destination.TerritoryId == territoryId));
    }
}
