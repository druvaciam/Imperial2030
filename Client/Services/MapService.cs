using System;
using System.Collections.Generic;
using System.Linq;
using Imperial2030.Client.Constants;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;

namespace Imperial2030.Client.Services;

public class MapService
{
    // Retrieve center coordinates for a territory
    public (double X, double Y) GetCenter(string territoryId)
    {
        if (MapCoordinates.TerritoryCenters.TryGetValue(territoryId, out var coords))
        {
            return coords;
        }
        // Fallback or log warning
        Console.WriteLine($"Warning: No coordinates for {territoryId}");
        return (0, 0);
    }

    // Get valid neighbors based on unit type
    public List<string> GetNeighbors(string territoryId, UnitType unitType)
    {
        if (!MapConnectivity.Adjacency.TryGetValue(territoryId, out var neighbors))
        {
            return new List<string>();
        }

        // Filter based on unit type later (e.g. Fleets can only go to sea or harbor)
        // For now, return all raw neighbors. 
        // TODO: Distinguish between Land and Sea neighbors for strict validation.
        
        return neighbors;
    }

    // Check if a move is valid (simplified 1-step adjacency)
    public bool IsValidMove(string fromId, string toId, UnitType unitType)
    {
        var neighbors = GetNeighbors(fromId, unitType);
        return neighbors.Contains(toId);
        
        // TODO: Add specific logic:
        // - Army: Land -> Land (or Convoy)
        // - Fleet: Sea -> Sea or Harbor -> Sea
    }
}
