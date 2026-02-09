using System.Collections.Generic;

namespace Imperial2030.Client.Constants;

public static class MapCoordinates
{
    // Approximate centers or anchor points for territories based on SVG paths
    public static readonly Dictionary<string, (double X, double Y)> TerritoryCenters = new()
    {
        // USA
        { "NewYork", (281, 229) },
        { "NewOrleans", (201, 287) },
        { "SanFrancisco", (153, 190) },
        { "Chicago", (242, 226) },

        // Europe
        { "London", (537, 160) },
        { "Paris", (550, 188) },
        { "Rome", (573, 225) },
        { "Berlin", (588, 174) },

        // Russia
        { "Moscow", (685, 166) },
        { "Murmansk", (652, 73) },
        { "Vladivostok", (1035, 195) },
        { "Novosibirsk", (842, 143) },

        // China
        { "Beijing", (892, 215) },
        { "Shanghai", (999, 280) },
        { "Chongqing", (923, 319) },
        { "Urumqi", (845, 210) },

        // India
        { "Mumbai", (823, 349) },
        { "NewDelhi", (862, 292) },
        { "Kolkata", (876, 344) },
        { "Chennai", (850, 360) },

        // Brazil
        { "Brasilia", (339, 488) },
        { "RioDeJaneiro", (380, 498) },
        { "Manaus", (287, 474) },
        { "Fortaleza", (350, 440) },

        // Independent Territories
        { "Switzerland", (577, 216) },
        { "Ukraine", (646, 213) },
        { "Korea", (999, 238) },
        { "Mongolia", (856, 187) },
        { "Kazakhstan", (828, 219) },
        { "Japan", (1050, 210) },
        { "Turkey", (703, 227) },
        { "Guinea", (508, 405) }
    };
}
