using System.Collections.Generic;

namespace Imperial2030.Client.Constants;

public static class MapCoordinates
{
    // Approximate centers or anchor points for territories based on SVG paths
    public static readonly Dictionary<string, (double X, double Y)> TerritoryCenters = new()
    {
        // USA
        { "NewYork", (225, 275) }, // Adjusted slightly Left/Up from m 232, 285
        { "NewOrleans", (232, 300) }, // Adjusted Down from m 232, 284
        { "SanFrancisco", (153, 190) },
        { "Chicago", (242, 226) },

        // Europe
        { "London", (530, 175) },
        { "Paris", (550, 188) },
        { "Rome", (573, 225) },
        { "Berlin", (615, 196) },

        // Russia
        { "Moscow", (685, 166) },
        { "Murmansk", (652, 73) },
        { "Vladivostok", (1000, 200) }, // Placeholder, missing in map
        { "Novosibirsk", (800, 150) }, // Placeholder, missing in map

        // China
        { "Beijing", (900, 280) }, // Placeholder
        { "Shanghai", (948, 323) },
        { "Chongqing", (880, 310) }, // Placeholder
        { "Urumqi", (800, 250) }, // Placeholder

        // India
        { "Mumbai", (827, 372) },
        { "NewDelhi", (800, 320) }, // Placeholder
        { "Kolkata", (850, 330) }, // Placeholder
        { "Chennai", (830, 400) }, // Placeholder

        // Brazil
        { "Brasilia", (323, 521) },
        { "RioDeJaneiro", (368, 465) },
        { "Manaus", (287, 474) },
        { "Fortaleza", (350, 440) }
    };
}
