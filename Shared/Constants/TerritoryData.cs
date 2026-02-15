using Imperial2030.Shared.Models;
using System.Collections.Generic;

namespace Imperial2030.Shared.Constants;

public static class TerritoryData
{
    // Define all territories with their IDs and properties
    public static readonly List<Territory> AllTerritories = new List<Territory>
    {
        // Russia
        new Territory { Id = "Moscow", Name = "Moscow", Nation = Nation.Russia, CityType = CityType.Brown },
        new Territory { Id = "Vladivostok", Name = "Vladivostok", Nation = Nation.Russia, CityType = CityType.LightBlue },
        new Territory { Id = "Murmansk", Name = "Murmansk", Nation = Nation.Russia, CityType = CityType.LightBlue },
        new Territory { Id = "Novosibirsk", Name = "Novosibirsk", Nation = Nation.Russia, CityType = CityType.Brown },

        // China
        new Territory { Id = "Beijing", Name = "Beijing", Nation = Nation.China, CityType = CityType.Brown },
        new Territory { Id = "Shanghai", Name = "Shanghai", Nation = Nation.China, CityType = CityType.LightBlue },
        new Territory { Id = "Chongqing", Name = "Chongqing", Nation = Nation.China, CityType = CityType.Brown },
        new Territory { Id = "Urumqi", Name = "Urumqi", Nation = Nation.China, CityType = CityType.Brown },

        // India
        new Territory { Id = "NewDelhi", Name = "New Delhi", Nation = Nation.India, CityType = CityType.Brown },
        new Territory { Id = "Mumbai", Name = "Mumbai", Nation = Nation.India, CityType = CityType.LightBlue },
        new Territory { Id = "Kolkata", Name = "Kolkata", Nation = Nation.India, CityType = CityType.LightBlue },
        new Territory { Id = "Chennai", Name = "Chennai", Nation = Nation.India, CityType = CityType.LightBlue },

        // Brazil
        new Territory { Id = "Brasilia", Name = "Brasilia", Nation = Nation.Brazil, CityType = CityType.Brown },
        new Territory { Id = "RioDeJaneiro", Name = "Rio De Janeiro", Nation = Nation.Brazil, CityType = CityType.LightBlue },
        new Territory { Id = "Manaus", Name = "Manaus", Nation = Nation.Brazil, CityType = CityType.Brown },
        new Territory { Id = "Fortaleza", Name = "Fortaleza", Nation = Nation.Brazil, CityType = CityType.LightBlue },

        // USA
        new Territory { Id = "NewYork", Name = "New York", Nation = Nation.USA, CityType = CityType.LightBlue },
        new Territory { Id = "SanFrancisco", Name = "San Francisco", Nation = Nation.USA, CityType = CityType.LightBlue },
        new Territory { Id = "NewOrleans", Name = "New Orleans", Nation = Nation.USA, CityType = CityType.LightBlue },
        new Territory { Id = "Chicago", Name = "Chicago", Nation = Nation.USA, CityType = CityType.Brown }, 

        // Europe
        new Territory { Id = "Berlin", Name = "Berlin", Nation = Nation.Europe, CityType = CityType.Brown },
        new Territory { Id = "London", Name = "London", Nation = Nation.Europe, CityType = CityType.LightBlue },
        new Territory { Id = "Paris", Name = "Paris", Nation = Nation.Europe, CityType = CityType.Brown },
        new Territory { Id = "Rome", Name = "Rome", Nation = Nation.Europe, CityType = CityType.LightBlue },

        // Independent Territories
        new Territory { Id = "Switzerland", Name = "Switzerland", Nation = null, CityType = CityType.None },
        new Territory { Id = "Ukraine", Name = "Ukraine", Nation = null, CityType = CityType.None },
        new Territory { Id = "Korea", Name = "Korea", Nation = null, CityType = CityType.None },
        new Territory { Id = "Mongolia", Name = "Mongolia", Nation = null, CityType = CityType.None },
        new Territory { Id = "Kazakhstan", Name = "Kazakhstan", Nation = null, CityType = CityType.None },
        new Territory { Id = "Japan", Name = "Japan", Nation = null, CityType = CityType.None },
        new Territory { Id = "Turkey", Name = "Turkey", Nation = null, CityType = CityType.None },
        new Territory { Id = "Guinea", Name = "Guinea", Nation = null, CityType = CityType.None },
        new Territory { Id = "Quebec", Name = "Quebec", Nation = null, CityType = CityType.None },
        new Territory { Id = "Mexico", Name = "Mexico", Nation = null, CityType = CityType.None },
        new Territory { Id = "Colombia", Name = "Colombia", Nation = null, CityType = CityType.None },
        new Territory { Id = "Afghanistan", Name = "Afghanistan", Nation = null, CityType = CityType.None },
        new Territory { Id = "Alaska", Name = "Alaska", Nation = null, CityType = CityType.None },
        new Territory { Id = "Canada", Name = "Canada", Nation = null, CityType = CityType.None },
        new Territory { Id = "Peru", Name = "Peru", Nation = null, CityType = CityType.None },
        new Territory { Id = "Argentina", Name = "Argentina", Nation = null, CityType = CityType.None },
        new Territory { Id = "Iran", Name = "Iran", Nation = null, CityType = CityType.None },
        new Territory { Id = "North-Africa", Name = "North-Africa", Nation = null, CityType = CityType.None },
        new Territory { Id = "Nigeria", Name = "Nigeria", Nation = null, CityType = CityType.None },
        new Territory { Id = "Congo", Name = "Congo", Nation = null, CityType = CityType.None },
        new Territory { Id = "South-Africa", Name = "South Africa", Nation = null, CityType = CityType.None },
        new Territory { Id = "East-Africa", Name = "East Africa", Nation = null, CityType = CityType.None },

        new Territory { Id = "NearEast", Name = "Near East", Nation = null, CityType = CityType.None },
        new Territory { Id = "Indochina", Name = "Indochina", Nation = null, CityType = CityType.None },
        new Territory { Id = "Indonesia", Name = "Indonesia", Nation = null, CityType = CityType.None },
        new Territory { Id = "Philippines", Name = "Philippines", Nation = null, CityType = CityType.None },
        new Territory { Id = "Australia", Name = "Australia", Nation = null, CityType = CityType.None },
        new Territory { Id = "NewZealand", Name = "New Zealand", Nation = null, CityType = CityType.None },
        
        // Sea Regions
        new Territory { Id = "MediterraneanSea", Name = "Mediterranean Sea", Nation = null, CityType = CityType.None, Type = TerritoryType.Sea },
        new Territory { Id = "NorthAtlantic", Name = "North Atlantic", Nation = null, CityType = CityType.None, Type = TerritoryType.Sea },
        new Territory { Id = "GulfOfGuinea", Name = "Gulf of Guinea", Nation = null, CityType = CityType.None, Type = TerritoryType.Sea },
        new Territory { Id = "NorthPacific", Name = "North Pacific", Nation = null, CityType = CityType.None, Type = TerritoryType.Sea },
        new Territory { Id = "SouthPacific", Name = "South Pacific", Nation = null, CityType = CityType.None, Type = TerritoryType.Sea },
        new Territory { Id = "SouthAtlantic", Name = "South Atlantic", Nation = null, CityType = CityType.None, Type = TerritoryType.Sea },
        new Territory { Id = "IndianOcean", Name = "Indian Ocean", Nation = null, CityType = CityType.None, Type = TerritoryType.Sea },

        new Territory { Id = "SeaOfJapan", Name = "Sea Of Japan", Nation = null, CityType = CityType.None, Type = TerritoryType.Sea },
        new Territory { Id = "ChinaSea", Name = "China Sea", Nation = null, CityType = CityType.None, Type = TerritoryType.Sea },
        new Territory { Id = "CaribbeanSea", Name = "Caribbean Sea", Nation = null, CityType = CityType.None, Type = TerritoryType.Sea },
        new Territory { Id = "TasmanSea", Name = "Tasman Sea", Nation = null, CityType = CityType.None, Type = TerritoryType.Sea },
    };
}
