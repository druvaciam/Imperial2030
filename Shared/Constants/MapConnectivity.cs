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
        { "Colombia", new List<string> { "Mexico", "Manaus", "Peru", "CaribbeanSea", "NorthPacific" } },
        { "Peru", new List<string> { "Colombia", "Manaus", "Brasilia", "Argentina", "SouthPacific" } },
        { "Manaus", new List<string> { "Colombia", "Peru", "Brasilia", "Fortaleza", "CaribbeanSea" } },
        { "Fortaleza", new List<string> { "Manaus", "Brasilia", "RioDeJaneiro", "CaribbeanSea" } },
        { "Brasilia", new List<string> { "Manaus", "Fortaleza", "RioDeJaneiro", "Argentina", "Peru" } },
        { "RioDeJaneiro", new List<string> { "Fortaleza", "Brasilia", "Argentina", "SouthAtlantic" } },
        { "Argentina", new List<string> { "RioDeJaneiro", "Brasilia", "Peru", "SouthPacific", "SouthAtlantic" } },

        // --- AFRICA ---
        { "North-Africa", new List<string> { "Guinea", "Nigeria", "MediterraneanSea", "NorthAtlantic", "IndianOcean", "East-Africa", "NearEast" } },
        { "Guinea", new List<string> { "North-Africa", "Nigeria", "NorthAtlantic", "GulfOfGuinea" } },
        { "Nigeria", new List<string> { "North-Africa", "East-Africa", "Guinea", "Congo", "GulfOfGuinea" } },
        { "Congo", new List<string> { "Nigeria", "South-Africa", "East-Africa", "GulfOfGuinea" } },
        { "South-Africa", new List<string> { "Congo", "East-Africa", "IndianOcean", "GulfOfGuinea"   } },
        { "East-Africa", new List<string> { "South-Africa", "North-Africa", "Congo", "Nigeria", "IndianOcean" } },

        // --- EUROPE ---
        { "London", new List<string> { "NorthAtlantic" } },
        { "Paris", new List<string> { "Berlin", "Rome", "MediterraneanSea", "NorthAtlantic" } }, 
        { "Rome", new List<string> { "Paris", "Berlin", "MediterraneanSea", "Turkey" } }, 
        { "Berlin", new List<string> { "Paris", "Rome", "Ukraine", "NorthAtlantic", "Murmansk" } }, 
        { "Ukraine", new List<string> { "Moscow", "Berlin" } },
        
        // --- RUSSIA & ASIA ---
        { "Moscow", new List<string> { "Ukraine", "Novosibirsk", "Murmansk", "Kazakhstan", "Turkey", "Iran" } },
        { "Murmansk", new List<string> { "Moscow", "Novosibirsk", "NorthAtlantic", "Berlin" } },
        { "Novosibirsk", new List<string> { "Moscow", "Murmansk", "Vladivostok", "Kazakhstan", "Mongolia" } },
        { "Vladivostok", new List<string> { "Novosibirsk", "Mongolia", "Beijing", "SeaOfJapan", "Korea" } },
        { "Mongolia", new List<string> { "Novosibirsk", "Vladivostok", "Beijing", "Urumqi", "Kazakhstan" } },
        { "Kazakhstan", new List<string> { "Moscow", "Novosibirsk", "Urumqi", "Mongolia", "Afghanistan" } },
        { "Urumqi", new List<string> { "Kazakhstan", "Beijing", "Chongqing", "NewDelhi", "Mongolia", "Afghanistan" } },
        { "Korea", new List<string> { "Beijing", "Vladivostok", "SeaOfJapan", "ChinaSea" } },
        { "Beijing", new List<string> { "Urumqi", "Chongqing", "Shanghai", "Vladivostok", "Korea", "Mongolia" } },
        { "Shanghai", new List<string> { "Beijing", "Chongqing", "ChinaSea" } },
        { "Chongqing", new List<string> { "Beijing", "Shanghai", "Urumqi", "Kolkata", "Indochina" } },
        
        // --- INDIA & MIDDLE EAST ---
        { "NewDelhi", new List<string> { "Kolkata", "Mumbai", "Urumqi", "Afghanistan" } },
        { "Mumbai", new List<string> { "NewDelhi", "Kolkata", "Chennai", "Iran", "IndianOcean" } },
        { "Chennai", new List<string> { "Mumbai", "Kolkata", "IndianOcean" } },
        { "Kolkata", new List<string> { "NewDelhi", "Mumbai", "Chennai", "Chongqing", "Indochina", "IndianOcean" } },
        { "Iran", new List<string> { "Turkey", "Afghanistan", "Mumbai", "IndianOcean", "NearEast", "Moscow" } },
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
        { "NorthAtlantic", new List<string> { "Canada", "NewYork", "Quebec", "Guinea", "North-Africa", "Paris", "London", "Berlin", "MediterraneanSea", "CaribbeanSea", "Murmansk", "GulfOfGuinea" } },
        { "MediterraneanSea", new List<string> { "NorthAtlantic", "IndianOcean", "Paris", "Rome", "Turkey", "North-Africa", "NearEast" } },
        { "IndianOcean", new List<string> { "South-Africa", "East-Africa", "North-Africa", "Mumbai", "Chennai", "Kolkata", "Indonesia", "GulfOfGuinea", "Indochina", "ChinaSea", "Iran", "MediterraneanSea", "NearEast", "SouthAtlantic", "TasmanSea" } },
        { "SouthAtlantic", new List<string> { "RioDeJaneiro", "Argentina", "IndianOcean", "GulfOfGuinea", "CaribbeanSea", "SouthPacific" } },
        { "NorthPacific", new List<string> { "Alaska", "Canada", "SanFrancisco", "Mexico", "Colombia", "SeaOfJapan", "ChinaSea", "SouthPacific", "CaribbeanSea" } },
        { "SouthPacific", new List<string> { "NorthPacific", "Peru", "Argentina", "TasmanSea", "ChinaSea", "SouthAtlantic" } },
        { "CaribbeanSea", new List<string> { "NewOrleans", "Colombia", "Manaus", "Fortaleza", "NorthAtlantic", "SouthAtlantic", "Mexico", "GulfOfGuinea", "NorthPacific" } },
        { "SeaOfJapan", new List<string> { "Vladivostok", "Japan", "NorthPacific", "Korea", "ChinaSea" } },
        { "ChinaSea", new List<string> { "Shanghai", "Indochina", "Indonesia", "Korea", "Philippines", "SeaOfJapan", "IndianOcean", "SouthPacific", "NorthPacific", "TasmanSea" } },
        { "TasmanSea", new List<string> { "Australia", "NewZealand", "SouthPacific", "Indonesia", "IndianOcean", "ChinaSea" } },
        { "GulfOfGuinea", new List<string> { "Nigeria", "Guinea", "Congo", "South-Africa", "SouthAtlantic", "NorthAtlantic", "CaribbeanSea", "IndianOcean" } },
    };

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
            // Fleet: Can ONLY move to Sea regions
            // (Whether starting from Land/Port or Sea, destination must be Sea)
            return neighbors.Where(n => {
                 var t = TerritoryData.AllTerritories.FirstOrDefault(x => x.Id == n);
                 return t != null && t.Type == Shared.Models.TerritoryType.Sea;
            });
        }
    }
    public static readonly List<(string Region1, string Region2, string ControllerId)> CanalLinks = new()
    {
        ("CaribbeanSea", "NorthPacific", "Colombia"), // Panama Canal
        ("MediterraneanSea", "IndianOcean", "North-Africa") // Suez Canal
    };
}
