using System.Collections.Generic;

namespace Imperial2030.Shared.Constants;

public static class MapConnectivity
{
    public static readonly Dictionary<string, List<string>> Adjacency = new() {
        // --- NORTH AMERICA (USA + Canada + Mexico) ---
        { "Alaska", new List<string> { "NorthPacific", "Canada" } }, // Check sphere connection name
        { "Canada", new List<string> { "Alaska", "Quebec", "Chicago", "SanFrancisco", "NorthPacific", "NorthAtlantic" } },
        { "Quebec", new List<string> { "Canada", "NewYork", "Chicago", "NorthAtlantic" } }, // US Home?
        { "NewYork", new List<string> { "Quebec", "Chicago", "NewOrleans", "NorthAtlantic" } },
        { "Chicago", new List<string> { "Canada", "Quebec", "NewYork", "NewOrleans", "SanFrancisco" } },
        { "SanFrancisco", new List<string> { "Canada", "Chicago", "NewOrleans", "Mexico", "NorthPacific" } },
        { "NewOrleans", new List<string> { "NewYork", "Chicago", "SanFrancisco", "Mexico", "CaribbeanSea" } },
        { "Mexico", new List<string> { "SanFrancisco", "NewOrleans", "Colombia", "NorthPacific", "CaribbeanSea" } },

        // --- SOUTH AMERICA (Brazil + Independent) ---
        { "Colombia", new List<string> { "Mexico", "Manaus", "Fortaleza", "Peru", "CaribbeanSea", "SouthPacific" } },
        { "Peru", new List<string> { "Colombia", "Manaus", "Brasilia", "Argentina", "SouthPacific" } },
        { "Manaus", new List<string> { "Colombia", "Peru", "Brasilia", "Fortaleza" } },
        { "Fortaleza", new List<string> { "Manaus", "Brasilia", "RioDeJaneiro", "Colombia", "NorthAtlantic", "SouthAtlantic" } },
        { "Brasilia", new List<string> { "Manaus", "Fortaleza", "RioDeJaneiro", "Argentina", "Peru" } },
        { "RioDeJaneiro", new List<string> { "Fortaleza", "Brasilia", "Argentina", "SouthAtlantic" } },
        { "Argentina", new List<string> { "RioDeJaneiro", "Brasilia", "Peru", "SouthPacific", "SouthAtlantic" } },

        // --- AFRICA ---
        { "North-Africa", new List<string> { "Guinea", "Nigeria", "MediterraneanSea", "NorthAtlantic" } },
        { "Guinea", new List<string> { "North-Africa", "Nigeria", "NorthAtlantic" } },
        { "Nigeria", new List<string> { "North-Africa", "Guinea", "Congo", "SouthAtlantic", "GulfOfGuinea" } }, // Check Gulf
        { "Congo", new List<string> { "Nigeria", "SouthAfrica", "EastAfrica", "SouthAtlantic" } },
        { "SouthAfrica", new List<string> { "Congo", "EastAfrica", "SouthAtlantic", "IndianOcean" } },
        { "EastAfrica", new List<string> { "SouthAfrica", "Congo", "Egypt", "IndianOcean", "MediterraneanSea" } }, // Check borders
        { "Egypt", new List<string> { "EastAfrica", "MediterraneanSea", "IndianOcean", "NearEast" } }, // Suez Logic separate?

        // --- EUROPE ---
        { "London", new List<string> { "NorthAtlantic", "NorthSea", "EnglishChannel" } },
        { "Paris", new List<string> { "Berlin", "Rome", "Switzerland", "EnglishChannel", "MediterraneanSea", "NorthAtlantic" } }, 
        { "Switzerland", new List<string> { "Paris", "Berlin", "Rome", "MediterraneanSea" } }, // Hub
        { "Rome", new List<string> { "Paris", "Berlin", "Switzerland", "MediterraneanSea" } }, 
        { "Berlin", new List<string> { "Paris", "Rome", "Switzerland", "Ukraine", "NorthSea", "Murmansk" } }, 
        { "Ukraine", new List<string> { "Moscow", "Berlin", "Turkey" } },

        
        // --- RUSSIA & ASIA ---
        { "Moscow", new List<string> { "Ukraine", "Novosibirsk", "Murmansk", "Kazakhstan" } },
        { "Murmansk", new List<string> { "Moscow", "Novosibirsk", "NorthAtlantic", "Berlin" } },
        { "Novosibirsk", new List<string> { "Moscow", "Murmansk", "Vladivostok", "Kazakhstan", "Urumqi", "Mongolia" } }, // Check specifics
        { "Vladivostok", new List<string> { "Novosibirsk", "Mongolia", "Beijing", "NorthPacific", "SeaOfJapan" } },
        { "Mongolia", new List<string> { "Novosibirsk", "Vladivostok", "Beijing", "Urumqi", "Kazakhstan" } },
        { "Kazakhstan", new List<string> { "Moscow", "Novosibirsk", "Urumqi", "Mongolia", "Afghanistan" } },
        { "Urumqi", new List<string> { "Novosibirsk", "Kazakhstan", "Beijing", "Chongqing", "NewDelhi", "Mongolia", "Afghanistan" } }, // China home?
        { "Beijing", new List<string> { "Urumqi", "Chongqing", "Shanghai", "Vladivostok", "Korea", "YellowSea", "Mongolia" } },
        { "Shanghai", new List<string> { "Beijing", "Chongqing", "YellowSea", "ChinaSea" } },
        { "Chongqing", new List<string> { "Beijing", "Shanghai", "Urumqi", "Kolkata", "Indochina" } },
        
        // --- INDIA & MIDDLE EAST ---
        { "NewDelhi", new List<string> { "Kolkata", "Mumbai", "Urumqi", "Afghanistan" } },
        { "Mumbai", new List<string> { "NewDelhi", "Kolkata", "Chennai", "Iran", "IndianOcean", "ArabianSea" } },
        { "Chennai", new List<string> { "Mumbai", "Kolkata", "IndianOcean", "BayOfBengal" } },
        { "Kolkata", new List<string> { "NewDelhi", "Mumbai", "Chennai", "Chongqing", "Indochina", "BayOfBengal" } },
        { "Iran", new List<string> { "Turkey", "Afghanistan", "Mumbai", "ArabianSea", "NearEast" } },
        { "Afghanistan", new List<string> { "Iran", "Kazakhstan", "Urumqi", "NewDelhi" } },
        { "Turkey", new List<string> { "Ukraine", "Iran", "NearEast", "MediterraneanSea" } },
        { "NearEast", new List<string> { "Turkey", "Egypt", "Iran", "MediterraneanSea", "IndianOcean" } },

        // --- OCEANIA / SE ASIA ---
        { "Indochina", new List<string> { "Kolkata", "Chongqing", "ChinaSea", "BayOfBengal", "Indonesia" } },
        { "Indonesia", new List<string> { "Indochina", "ChinaSea", "IndianOcean", "Australia", "Philippines" } }, // check Is Land?
        { "Philippines", new List<string> { "ChinaSea", "NorthPacific" } },
        { "Japan", new List<string> { "SeaOfJapan", "NorthPacific" } },
        { "Australia", new List<string> { "Indonesia", "IndianOcean", "SouthPacific" } },
        { "NewZealand", new List<string> { "SouthPacific", "TasmanSea" } },

        // --- SEA REGIONS (Interconnectivity) ---
        { "NorthAtlantic", new List<string> { "Canada", "NewYork", "Fortaleza", "RioDeJaneiro", "Guinea", "Paris", "London", "NorthSea", "MediterraneanSea", "SouthAtlantic", "Murmansk" } },
        { "MediterraneanSea", new List<string> { "NorthAtlantic", "Paris", "Rome", "Switzerland", "Turkey", "Egypt", "EastAfrica", "North-Africa" } }, // Check EastAfrica/Suez
        { "NorthSea", new List<string> { "NorthAtlantic", "London", "Berlin" } },

        
        { "IndianOcean", new List<string> { "SouthAfrica", "EastAfrica", "Egypt", "Mumbai", "Chennai", "Kolkata", "Indonesia", "Australia", "BayOfBengal", "ArabianSea" } },
        { "SouthAtlantic", new List<string> { "NorthAtlantic", "Fortaleza", "RioDeJaneiro", "Argentina", "SouthAfrica", "Congo", "Nigeria", "Guinea", "IndianOcean" } },
        { "NorthPacific", new List<string> { "Alaska", "Canada", "SanFrancisco", "Mexico", "Japan", "Vladivostok", "Philippines", "SouthPacific" } },
        { "SouthPacific", new List<string> { "NorthPacific", "SanFrancisco", "Colombia", "Peru", "Argentina", "Australia", "NewZealand", "TasmanSea" } },
        { "CaribbeanSea", new List<string> { "NewOrleans", "Colombia", "NorthAtlantic", "Mexico" } },
        
        // Specific smaller seas
        { "SeaOfJapan", new List<string> { "Vladivostok", "Japan", "NorthPacific", "YellowSea" } },
        { "YellowSea", new List<string> { "Beijing", "Shanghai", "Korea", "SeaOfJapan", "ChinaSea" } },
        { "ChinaSea", new List<string> { "Shanghai", "Indochina", "Indonesia", "Philippines", "YellowSea", "BayOfBengal" } },
        { "BayOfBengal", new List<string> { "Kolkata", "Chennai", "Indochina", "IndianOcean", "ChinaSea" } },
        { "ArabianSea", new List<string> { "Mumbai", "Iran", "IndianOcean" } }, // Iran port?
        { "TasmanSea", new List<string> { "Australia", "NewZealand", "SouthPacific" } },
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
