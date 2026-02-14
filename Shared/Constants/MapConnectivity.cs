using System.Collections.Generic;

namespace Imperial2030.Shared.Constants;

public static class MapConnectivity
{
    public static readonly Dictionary<string, List<string>> Adjacency = new() {
        // --- NORTH AMERICA (USA + Canada + Mexico) ---
        { "Alaska", new List<string> { "NorthPacific", "Canada" } },
        { "Canada", new List<string> { "Alaska", "Quebec", "NewYork", "Chicago", "SanFrancisco", "NorthPacific", "NorthAtlantic" } },
        { "Quebec", new List<string> { "Canada", "NewYork", "NorthAtlantic" } },
        { "NewYork", new List<string> { "Quebec", "Chicago", "Canada", "NewOrleans", "NorthAtlantic" } },
        { "Chicago", new List<string> { "Canada", "NewYork", "NewOrleans", "SanFrancisco" } },
        { "SanFrancisco", new List<string> { "Canada", "Chicago", "NewOrleans", "Mexico", "NorthPacific" } },
        { "NewOrleans", new List<string> { "NewYork", "Chicago", "SanFrancisco", "Mexico", "CaribbeanSea" } },
        { "Mexico", new List<string> { "SanFrancisco", "NewOrleans", "Colombia", "NorthPacific", "CaribbeanSea" } },

        // --- SOUTH AMERICA (Brazil + Independent) ---
        { "Colombia", new List<string> { "Mexico", "Manaus", "Peru", "CaribbeanSea", "SouthPacific" } },
        { "Peru", new List<string> { "Colombia", "Manaus", "Brasilia", "Argentina", "SouthPacific" } },
        { "Manaus", new List<string> { "Colombia", "Peru", "Brasilia", "Fortaleza", "CaribbeanSea" } },
        { "Fortaleza", new List<string> { "Manaus", "Brasilia", "RioDeJaneiro", "CaribbeanSea" } },
        { "Brasilia", new List<string> { "Manaus", "Fortaleza", "RioDeJaneiro", "Argentina", "Peru" } },
        { "RioDeJaneiro", new List<string> { "Fortaleza", "Brasilia", "Argentina", "SouthAtlantic" } },
        { "Argentina", new List<string> { "RioDeJaneiro", "Brasilia", "Peru", "SouthPacific", "SouthAtlantic" } },

        // --- AFRICA ---
        { "North-Africa", new List<string> { "Guinea", "Nigeria", "MediterraneanSea", "NorthAtlantic", "East-Africa", "NearEast" } },
        { "Guinea", new List<string> { "North-Africa", "Nigeria", "NorthAtlantic", "GulfOfGuinea" } },
        { "Nigeria", new List<string> { "North-Africa", "Guinea", "Congo", "SouthAtlantic", "GulfOfGuinea" } },
        { "Congo", new List<string> { "Nigeria", "South-Africa", "East-Africa", "SouthAtlantic", "GulfOfGuinea" } },
        { "South-Africa", new List<string> { "Congo", "East-Africa", "SouthAtlantic", "IndianOcean" } },
        { "East-Africa", new List<string> { "South-Africa", "North-Africa", "Congo", "IndianOcean", "MediterraneanSea" } },

        // --- EUROPE ---
        { "London", new List<string> { "NorthAtlantic" } },
        { "Paris", new List<string> { "Berlin", "Rome", "Switzerland", "MediterraneanSea", "NorthAtlantic" } }, 
        { "Switzerland", new List<string> { "Paris", "Berlin", "Rome", "MediterraneanSea" } },
        { "Rome", new List<string> { "Paris", "Berlin", "Switzerland", "MediterraneanSea", "Turkey" } }, 
        { "Berlin", new List<string> { "Paris", "Rome", "Switzerland", "Ukraine", "NorthAtlantic", "Murmansk" } }, 
        { "Ukraine", new List<string> { "Moscow", "Berlin" } },
        
        // --- RUSSIA & ASIA ---
        { "Moscow", new List<string> { "Ukraine", "Novosibirsk", "Murmansk", "Kazakhstan", "Turkey" } },
        { "Murmansk", new List<string> { "Moscow", "Novosibirsk", "NorthAtlantic", "Berlin" } },
        { "Novosibirsk", new List<string> { "Moscow", "Murmansk", "Vladivostok", "Kazakhstan", "Urumqi", "Mongolia" } },
        { "Vladivostok", new List<string> { "Novosibirsk", "Mongolia", "Beijing", "NorthPacific", "SeaOfJapan" } },
        { "Mongolia", new List<string> { "Novosibirsk", "Vladivostok", "Beijing", "Urumqi", "Kazakhstan" } },
        { "Kazakhstan", new List<string> { "Moscow", "Novosibirsk", "Urumqi", "Mongolia", "Afghanistan" } },
        { "Urumqi", new List<string> { "Novosibirsk", "Kazakhstan", "Beijing", "Chongqing", "NewDelhi", "Mongolia", "Afghanistan" } },
        { "Beijing", new List<string> { "Urumqi", "Chongqing", "Shanghai", "Vladivostok", "Korea", "SeaOfJapan", "Mongolia" } },
        { "Shanghai", new List<string> { "Beijing", "Chongqing", "SeaOfJapan", "ChinaSea" } },
        { "Chongqing", new List<string> { "Beijing", "Shanghai", "Urumqi", "Kolkata", "Indochina" } },
        
        // --- INDIA & MIDDLE EAST ---
        { "NewDelhi", new List<string> { "Kolkata", "Mumbai", "Urumqi", "Afghanistan" } },
        { "Mumbai", new List<string> { "NewDelhi", "Kolkata", "Chennai", "Iran", "IndianOcean" } },
        { "Chennai", new List<string> { "Mumbai", "Kolkata", "IndianOcean" } },
        { "Kolkata", new List<string> { "NewDelhi", "Mumbai", "Chennai", "Chongqing", "Indochina", "IndianOcean" } },
        { "Iran", new List<string> { "Turkey", "Afghanistan", "Mumbai", "IndianOcean", "NearEast" } },
        { "Afghanistan", new List<string> { "Iran", "Kazakhstan", "Urumqi", "NewDelhi" } },
        { "Turkey", new List<string> { "Rome", "Moscow", "Iran", "NearEast", "MediterraneanSea" } },
        { "NearEast", new List<string> { "Turkey", "Iran", "MediterraneanSea", "IndianOcean", "North-Africa" } },

        // --- OCEANIA / SE ASIA ---
        { "Indochina", new List<string> { "Kolkata", "Chongqing", "ChinaSea", "IndianOcean" } },
        { "Indonesia", new List<string> { "ChinaSea", "IndianOcean", "TasmanSea" } },
        { "Philippines", new List<string> { "ChinaSea" } },
        { "Japan", new List<string> { "SeaOfJapan" } },
        { "Australia", new List<string> { "TasmanSea" } },
        { "NewZealand", new List<string> { "TasmanSea" } },

        // --- SEA REGIONS (Interconnectivity) ---
        { "NorthAtlantic", new List<string> { "Canada", "NewYork", "Fortaleza", "RioDeJaneiro", "Guinea", "Paris", "London", "Berlin", "MediterraneanSea", "CaribbeanSea", "Murmansk", "GulfOfGuinea" } },
        { "MediterraneanSea", new List<string> { "NorthAtlantic", "IndianOcean", "Paris", "Rome", "Switzerland", "Turkey", "East-Africa", "North-Africa", "NearEast" } },
        { "IndianOcean", new List<string> { "South-Africa", "East-Africa", "Mumbai", "Chennai", "Kolkata", "Indonesia", "GulfOfGuinea", "Indochina", "ChinaSea", "Iran", "MediterraneanSea", "NearEast" } },
        { "SouthAtlantic", new List<string> { "Fortaleza", "RioDeJaneiro", "Argentina", "South-Africa", "Congo", "Nigeria", "Guinea", "IndianOcean", "GulfOfGuinea" } },
        { "NorthPacific", new List<string> { "Alaska", "Canada", "SanFrancisco", "Mexico", "Vladivostok", "SeaOfJapan", "ChinaSea", "SouthPacific" } },
        { "SouthPacific", new List<string> { "NorthPacific", "SanFrancisco", "Colombia", "Peru", "Argentina", "TasmanSea", "ChinaSea" } },
        { "CaribbeanSea", new List<string> { "NewOrleans", "Colombia", "NorthAtlantic", "SouthAtlantic", "Mexico", "GulfOfGuinea" } },
        { "SeaOfJapan", new List<string> { "Vladivostok", "Japan", "NorthPacific", "Beijing", "Shanghai", "Korea", "ChinaSea" } },
        { "ChinaSea", new List<string> { "Shanghai", "Indochina", "Indonesia", "Philippines", "SeaOfJapan", "IndianOcean", "SouthPacific", "NorthPacific" } },
        { "TasmanSea", new List<string> { "Australia", "NewZealand", "SouthPacific", "Indonesia" } },
        { "GulfOfGuinea", new List<string> { "Nigeria", "Guinea", "Congo", "SouthAtlantic", "NorthAtlantic", "CaribbeanSea", "IndianOcean" } },
    };

    // Helper for sea vs land distinction if needed (though TerritoryData.cs has CityType which helps)
    public static IEnumerable<string> GetNeighbors(string territoryId, bool isFleet)
    {
        if (!Adjacency.TryGetValue(territoryId, out var neighbors))
            return Enumerable.Empty<string>();

        if (!isFleet)
        {
            // Army: Only Land neighbors
            return neighbors.Where(n => {
                 var t = TerritoryData.AllTerritories.FirstOrDefault(x => x.Id == n);
                 return t != null && t.Type == Shared.Models.TerritoryType.Land;
            });
        }
        else
        {
            // Fleet
            var currentT = TerritoryData.AllTerritories.FirstOrDefault(x => x.Id == territoryId);
            if (currentT == null) return Enumerable.Empty<string>();

            if (currentT.Type == Shared.Models.TerritoryType.Land)
            {
                // Fleet in Port: Can only move to Sea
                return neighbors.Where(n => {
                     var t = TerritoryData.AllTerritories.FirstOrDefault(x => x.Id == n);
                     return t != null && t.Type == Shared.Models.TerritoryType.Sea;
                });
            }
            else
            {
                // Fleet in Sea: Can move to Sea or Land (Port)
                return neighbors;
            }
        }
    }
}
