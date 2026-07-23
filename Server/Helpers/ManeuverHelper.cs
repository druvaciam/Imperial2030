using System;
using System.Collections.Generic;
using System.Linq;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;

namespace Imperial2030.Server.Helpers
{
    public class ArmyDestination
    {
        public string TerritoryId { get; set; } = "";
        public bool IsRail { get; set; }
        public bool IsConvoy { get; set; }
        public List<Unit> ConvoyFleets { get; set; } = new List<Unit>();
    }

    public static class ManeuverHelper
    {
        public static List<string> GetRailReachableTerritories(Game game, string startId, Nation nation, bool includeExitPoints = true)
        {
            var reachable = new HashSet<string>();
            var queue = new Queue<(string id, int cost)>();
            var minCosts = new Dictionary<string, int>();

            queue.Enqueue((startId, 0));
            minCosts[startId] = 0;

            while (queue.Count > 0)
            {
                var (currentId, currentCost) = queue.Dequeue();

                if (minCosts.TryGetValue(currentId, out var recordedCost) && currentCost > recordedCost)
                    continue;

                if (MapConnectivity.Adjacency.TryGetValue(currentId, out var neighbors))
                {
                    var currentDef = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == currentId);
                    bool isCurrentHome = currentDef?.Nation == nation;

                    foreach (var neighborId in neighbors)
                    {
                        var neighborDef = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == neighborId);
                        if (neighborDef == null || neighborDef.Type != TerritoryType.Land) continue;

                        bool isNeighborHome = neighborDef.Nation == nation;
                        
                        var tState = game.TerritoryStates?.FirstOrDefault(ts => ts.TerritoryId == neighborId);
                        var effectiveController = tState?.Controller ?? neighborDef.Nation;
                        bool isControlledByUs = effectiveController == nation;
                        bool hasHostileUnits = game.Units.Any(u => u.TerritoryId == neighborId && u.Nation != nation && u.UnitType == UnitType.Army && u.IsHostile);

                        bool isRailStep = false;
                        if (isCurrentHome && isNeighborHome && isControlledByUs && !hasHostileUnits)
                        {
                            isRailStep = true;
                        }

                        int edgeCost = isRailStep ? 0 : 1;
                        int newCost = currentCost + edgeCost;

                        if (!includeExitPoints && !isNeighborHome)
                        {
                           continue;
                        }

                        if (newCost > 1) continue;

                        reachable.Add(neighborId);

                        if (!minCosts.TryGetValue(neighborId, out var oldCost) || newCost < oldCost)
                        {
                            minCosts[neighborId] = newCost;
                            queue.Enqueue((neighborId, newCost));
                        }
                    }
                }
            }
            return reachable.ToList();
        }

        public static bool CanMoveByRail(Game game, string startId, string endId, Nation nation)
        {
            var reachable = GetRailReachableTerritories(game, startId, nation);
            return reachable.Contains(endId);
        }

        public static List<Unit>? GetConvoyFleets(Game game, string startId, string destId, List<Nation> usableFleetNations)
        {
            var destinations = GetAllReachableArmyDestinations(game, startId, usableFleetNations);
            var dest = destinations.FirstOrDefault(d => d.TerritoryId == destId && d.IsConvoy);
            return dest?.ConvoyFleets;
        }

        public static List<ArmyDestination> GetAllReachableArmyDestinations(Game game, string startId, List<Nation> usableFleetNations)
        {
            var results = new Dictionary<string, ArmyDestination>();
            var armyNation = game.Units.FirstOrDefault(u => u.TerritoryId == startId && u.UnitType == UnitType.Army)?.Nation;
            if (!armyNation.HasValue) return results.Values.ToList();

            // 1. Immediate Land Neighbors (Cost 1 or less implicitly)
            if (MapConnectivity.Adjacency.TryGetValue(startId, out var immediateNeighbors))
            {
                foreach (var n in immediateNeighbors)
                {
                    var def = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == n);
                    if (def != null && def.Type == TerritoryType.Land)
                    {
                        results[n] = new ArmyDestination { TerritoryId = n, IsRail = false, IsConvoy = false };
                    }
                }
            }

            // 2. Rail Reachable (Cost 0/1 within Home)
            var railReachable = GetRailReachableTerritories(game, startId, armyNation.Value, includeExitPoints: true);
            foreach (var r in railReachable)
            {
                if (r != startId)
                {
                    results[r] = new ArmyDestination { TerritoryId = r, IsRail = true, IsConvoy = false };
                }
            }

            // 3. Convoy Reachable (via usableFleetNations fleets)
            var launchPoints = new HashSet<string> { startId };
            var startDef = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == startId);
            if (startDef != null && startDef.Type == TerritoryType.Land)
            {
                bool isStartHome = startDef.Nation == armyNation.Value;
                var startState = game.TerritoryStates?.FirstOrDefault(ts => ts.TerritoryId == startId);
                bool isStartControlled = (isStartHome && (startState == null || startState.Controller == armyNation.Value)) ||
                                         (startState != null && startState.Controller == armyNation.Value);
                bool startHasHostiles = game.Units.Any(u => u.TerritoryId == startId && u.Nation != armyNation.Value && u.IsHostile);

                if ((isStartHome || isStartControlled) && !startHasHostiles)
                {
                    var railOnly = GetRailReachableTerritories(game, startId, armyNation.Value, includeExitPoints: false);
                    foreach (var r in railOnly) launchPoints.Add(r);
                }
            }

            var queue = new Queue<(string Location, List<Unit> Fleets)>();
            var visitedSea = new HashSet<string>();

            foreach (var lp in launchPoints)
            {
                queue.Enqueue((lp, new List<Unit>()));
            }

            while (queue.Count > 0)
            {
                var (currentId, currentFleets) = queue.Dequeue();

                if (!MapConnectivity.Adjacency.TryGetValue(currentId, out var neighbors)) continue;

                foreach (var neighbor in neighbors)
                {
                    var canal = MapConnectivity.CanalLinks.FirstOrDefault(c => 
                        (c.Region1 == currentId && c.Region2 == neighbor) ||
                        (c.Region1 == neighbor && c.Region2 == currentId));

                    if (canal != default)
                    {
                       var tState = game.TerritoryStates?.FirstOrDefault(ts => ts.TerritoryId == canal.ControllerId);
                       if (tState != null && tState.Controller != null && tState.Controller != armyNation.Value)
                       {
                           continue; // Canal blocked
                       }
                    }

                    var neighborDef = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == neighbor);
                    if (neighborDef == null) continue;

                    if (neighborDef.Type == TerritoryType.Sea)
                    {
                        if (visitedSea.Contains(neighbor)) continue;

                        var fleet = game.Units.FirstOrDefault(u => 
                            usableFleetNations.Contains(u.Nation) && 
                            u.TerritoryId == neighbor && 
                            u.UnitType == UnitType.Fleet && 
                            !u.HasConvoyed &&
                            !currentFleets.Contains(u));

                        if (fleet != null)
                        {
                            visitedSea.Add(neighbor);
                            queue.Enqueue((neighbor, new List<Unit>(currentFleets) { fleet }));
                        }
                    }
                    else if (neighborDef.Type == TerritoryType.Land && neighbor != startId && currentFleets.Count > 0)
                    {
                        // Found a land destination via sea
                        if (!results.ContainsKey(neighbor) || !results[neighbor].IsConvoy)
                        {
                            results[neighbor] = new ArmyDestination { TerritoryId = neighbor, IsRail = false, IsConvoy = true, ConvoyFleets = currentFleets };
                        }
                    }
                }
            }

            return results.Values.ToList();
        }

        public static List<Unit>? ValidateSpecificConvoyFleets(Game game, string startId, string destId, Nation armyNation, List<Guid> fleetIds, List<Nation> usableFleetNations)
        {
            var fleets = new List<Unit>();
            foreach(var fId in fleetIds)
            {
                var f = game.Units.FirstOrDefault(u => u.Id == fId);
                if(f == null) return null;
                if (!usableFleetNations.Contains(f.Nation)) return null;
                if (f.UnitType != UnitType.Fleet) return null;
                if (f.HasConvoyed) return null;
                fleets.Add(f);
            }

            var launchPoints = new HashSet<string> { startId };
            var startDef = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == startId);
            if (startDef != null && startDef.Type == TerritoryType.Land)
            {
                bool isStartHome = startDef.Nation == armyNation;
                var startState = game.TerritoryStates?.FirstOrDefault(ts => ts.TerritoryId == startId);
                var effectiveController = startState?.Controller ?? startDef.Nation;
                bool isControlledByUs = effectiveController == armyNation;
                bool startHasHostiles = game.Units.Any(u => u.TerritoryId == startId && u.Nation != armyNation && u.IsHostile);

                if (isStartHome && isControlledByUs && !startHasHostiles)
                {
                    var railReachable = GetRailReachableTerritories(game, startId, armyNation, includeExitPoints: false);
                    foreach (var r in railReachable) launchPoints.Add(r);
                }
            }

            var queue = new Queue<string>();
            var visited = new HashSet<string>();
            
            foreach (var lp in launchPoints)
            {
                queue.Enqueue(lp);
                visited.Add(lp);
            }

            while(queue.Count > 0)
            {
                var currentId = queue.Dequeue();
                
                if (!MapConnectivity.Adjacency.TryGetValue(currentId, out var neighbors)) continue;

                foreach(var neighbor in neighbors)
                {
                    if(visited.Contains(neighbor)) continue;

                    var neighborDef = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == neighbor);
                    if (neighborDef == null) continue;

                    if (neighborDef.Type == TerritoryType.Sea)
                    {
                        if (fleets.Any(f => f.TerritoryId == neighbor))
                        {
                            visited.Add(neighbor);
                            queue.Enqueue(neighbor);
                        }
                    }
                    else if (neighborDef.Type == TerritoryType.Land)
                    {
                        if (neighbor == destId) return fleets;
                    }
                }
            }

            return null;
        }
    }
}
