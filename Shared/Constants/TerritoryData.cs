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
    };
}
