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
        public static List<string> GetRailReachableTerritories(Game game, string startId, Nation nation, bool includeExitPoints = true, bool pureRailOnly = false)
            => TraverseRail(game, startId, nation, includeExitPoints, pureRailOnly).Reachable.ToList();

        /// <summary>
        /// The rail traversal shared by GetRailReachableTerritories and GetRailPath. Identical rules to
        /// what GetRailReachableTerritories has always applied; it additionally records, for each
        /// territory it settles on, which one it was reached from, so the route can be walked back.
        /// </summary>
        private static (HashSet<string> Reachable, Dictionary<string, string> CameFrom) TraverseRail(
            Game game, string startId, Nation nation, bool includeExitPoints, bool pureRailOnly)
        {
            var reachable = new HashSet<string>();
            var cameFrom = new Dictionary<string, string>();
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
                        bool hasHostileUnits = game.Units.Any(u => u.TerritoryId == neighborId && u.Nation != nation && u.UnitType == UnitType.Army && u.IsHostile);

                        bool isRailStep = false;
                        if (isCurrentHome && isNeighborHome && !hasHostileUnits)
                        {
                            isRailStep = true;
                        }

                        int edgeCost = isRailStep ? 0 : 1;
                        int newCost = currentCost + edgeCost;

                        if (pureRailOnly && newCost > 0)
                        {
                            continue;
                        }

                        if (!includeExitPoints && !isNeighborHome)
                        {
                           continue;
                        }

                        if (newCost > 1) continue;

                        reachable.Add(neighborId);

                        if (!minCosts.TryGetValue(neighborId, out var oldCost) || newCost < oldCost)
                        {
                            minCosts[neighborId] = newCost;
                            cameFrom[neighborId] = currentId;
                            queue.Enqueue((neighborId, newCost));
                        }
                    }
                }
            }
            return (reachable, cameFrom);
        }

        /// <summary>
        /// The territories a rail move passes THROUGH, in travel order, excluding origin and destination.
        /// Empty when the destination is not rail-reachable, or when it is reached in a single step and
        /// there is nothing in between. Recorded in the action log so a replay or the map can draw the
        /// route the army actually took instead of guessing one from origin and destination.
        /// </summary>
        public static List<string> GetRailPath(Game game, string startId, string endId, Nation nation)
        {
            var (reachable, cameFrom) = TraverseRail(game, startId, nation, includeExitPoints: true, pureRailOnly: false);
            if (!reachable.Contains(endId)) return new List<string>();

            var path = new List<string>();
            var current = endId;
            // Walk back to the origin, collecting only what lies strictly between the two ends.
            while (cameFrom.TryGetValue(current, out var previous) && previous != startId)
            {
                path.Add(previous);
                current = previous;
                if (path.Count > TerritoryData.AllTerritories.Count) break;   // guard against a cycle
            }
            path.Reverse();
            return path;
        }

        public static bool CanMoveByRail(Game game, string startId, string endId, Nation nation)
        {
            var reachable = GetRailReachableTerritories(game, startId, nation);
            return reachable.Contains(endId);
        }

        /// <summary>
        /// How an army reaches a destination. A convoy is a last resort: it is for destinations that
        /// cannot be reached overland (Imperial-2030-Rules.pdf, "Maneuver").
        /// </summary>
        public enum ArmyMoveMode { AdjacentStep, Rail, Convoy }

        /// <summary>
        /// Which of the three the army must use, in the order the rules put them: a step to a neighbouring
        /// territory, else rail, and a convoy only when neither reaches it.
        ///
        /// Single-sourced deliberately. MoveArmy and the bot's maneuver each used to decide this for
        /// themselves and drifted apart: the bot picked a convoy whenever one merely EXISTED, so it
        /// shipped armies between adjacent territories and burned the carrier fleets doing it, while
        /// MoveArmy correctly walked them. Anything choosing a move mode must call this.
        /// </summary>
        public static ArmyMoveMode DetermineArmyMoveMode(Game game, string originId, string destinationId, Nation nation)
        {
            if (MapConnectivity.GetNeighbors(originId, false).Contains(destinationId)) return ArmyMoveMode.AdjacentStep;
            if (CanMoveByRail(game, originId, destinationId, nation)) return ArmyMoveMode.Rail;
            return ArmyMoveMode.Convoy;
        }

        /// <summary>
        /// What the move passes through, for the action log: nothing for a step to a neighbour, the rail
        /// hops for a rail move, and the boarding point plus sea regions for a convoy. See
        /// GameLogger.LogUnitMove's routeVia parameter for why this is recorded rather than derived later.
        /// </summary>
        public static List<string>? BuildMoveRoute(Game game, string originId, string destinationId, Nation nation,
                                                   ArmyMoveMode mode, List<Unit>? convoyFleets)
            => mode switch
            {
                ArmyMoveMode.Rail => GetRailPath(game, originId, destinationId, nation),
                ArmyMoveMode.Convoy when convoyFleets != null => BuildConvoyRoute(game, originId, nation, convoyFleets),
                _ => null
            };

        /// <summary>
        /// The full route a convoyed army travelled, in travel order, excluding origin and destination:
        /// any rail hops to the coast, the territory it boarded at, then every sea region it was carried
        /// through. None of this is recoverable afterwards - the carrying fleets are flagged HasConvoyed
        /// the moment the move completes, and several routes can join the same pair of endpoints - so it
        /// is recorded at the time of the move.
        /// </summary>
        public static List<string> BuildConvoyRoute(Game game, string startId, Nation nation, List<Unit> usedFleets)
        {
            var seaRoute = usedFleets.Select(f => f.TerritoryId).ToList();
            if (seaRoute.Count == 0) return new List<string>();

            // Already on the coast that first fleet sits on: it boarded where it stood.
            if (MapConnectivity.GetNeighbors(startId, true).Contains(seaRoute[0])) return seaRoute;

            var embarkation = GetRailReachableTerritories(game, startId, nation, includeExitPoints: false)
                .FirstOrDefault(t => MapConnectivity.GetNeighbors(t, true).Contains(seaRoute[0]));
            if (embarkation == null) return seaRoute;

            var route = GetRailPath(game, startId, embarkation, nation);
            route.Add(embarkation);
            route.AddRange(seaRoute);
            return route;
        }

        public static List<Unit>? GetConvoyFleets(Game game, string startId, string destId, Nation armyNation)
        {
            var destinations = GetAllReachableArmyDestinations(game, startId, armyNation);
            var dest = destinations.FirstOrDefault(d => d.TerritoryId == destId && d.IsConvoy);
            return dest?.ConvoyFleets;
        }

        public static List<ArmyDestination> GetAllReachableArmyDestinations(Game game, string startId, Nation armyNation)
        {
            var results = new Dictionary<string, ArmyDestination>();

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
            var railReachable = GetRailReachableTerritories(game, startId, armyNation, includeExitPoints: true);
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
                bool isStartHome = startDef.Nation == armyNation;
                bool startHasHostiles = game.Units.Any(u => u.TerritoryId == startId && u.Nation != armyNation && u.IsHostile);

                if (isStartHome && !startHasHostiles)
                {
                    var railOnly = GetRailReachableTerritories(game, startId, armyNation, includeExitPoints: false, pureRailOnly: true);
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
                       if (tState != null && tState.Controller != null && tState.Controller != armyNation)
                       {
                           var controllerNation = tState.Controller.Value;
                           var controllerState = game.NationStates.FirstOrDefault(ns => ns.Nation == controllerNation);
                           var armyState = game.NationStates.FirstOrDefault(ns => ns.Nation == armyNation);
                           
                           if (controllerState == null || armyState == null || controllerState.ControllerId != armyState.ControllerId)
                           {
                               continue; // Canal blocked
                           }
                       }
                    }

                    var neighborDef = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == neighbor);
                    if (neighborDef == null) continue;

                    if (neighborDef.Type == TerritoryType.Sea)
                    {
                        if (visitedSea.Contains(neighbor)) continue;

                        var fleet = game.Units.FirstOrDefault(u => 
                            u.Nation == armyNation && 
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

        public static List<Unit>? ValidateSpecificConvoyFleets(Game game, string startId, string destId, Nation armyNation, List<Guid> fleetIds)
        {
            var destinations = GetAllReachableArmyDestinations(game, startId, armyNation);
            var fleets = new List<Unit>();
            foreach(var fId in fleetIds)
            {
                var f = game.Units.FirstOrDefault(u => u.Id == fId);
                if(f == null) return null;
                if (f.Nation != armyNation) return null;
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

