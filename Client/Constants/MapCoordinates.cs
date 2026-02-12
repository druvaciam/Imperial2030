using System.Collections.Generic;

namespace Imperial2030.Client.Constants;

public static class MapCoordinates
{
    // Approximate centers or anchor points for territories based on SVG paths
    public static readonly Dictionary<string, (double X, double Y)> TerritoryCenters = new()
    {
        // USA
        { "NewYork", (281, 229) },
        { "NewOrleans", (195, 287) },
        { "SanFrancisco", (83, 240) },
        { "Chicago", (225, 215) },

        // Europe
        { "London", (537, 160) },
        { "Paris", (550, 188) },
        { "Rome", (588, 235) },
        { "Berlin", (588, 172) },

        // Russia
        { "Moscow", (685, 166) },
        { "Murmansk", (652, 73) },
        { "Vladivostok", (1035, 195) },
        { "Novosibirsk", (842, 143) },

        // China
        { "Beijing", (957, 226) },
        { "Shanghai", (1000, 280) },
        { "Chongqing", (935, 298) },
        { "Urumqi", (845, 210) },

        // India
        { "Mumbai", (823, 349) },
        { "NewDelhi", (828, 279) },
        { "Kolkata", (890, 329) },
        { "Chennai", (850, 360) },

        // Brazil
        { "Brasilia", (341, 488) },
        { "RioDeJaneiro", (380, 500) },
        { "Manaus", (309, 443) },
        { "Fortaleza", (395, 433) },

        // Independent Territories
        { "Switzerland", (577, 216) },
        { "Ukraine", (646, 213) },
        { "Korea", (999, 238) },
        { "Mongolia", (856, 187) },
        { "Kazakhstan", (828, 219) },
        { "Japan", (1050, 210) },
        { "Turkey", (703, 227) },
        { "Guinea", (508, 405) },
        { "NorthAtlantic", (405, 250) },
        { "Quebec", (315, 155) },
        { "Mexico", (135, 333) },
        { "Colombia", (237, 394) },
        { "Afghanistan", (775, 247) },
        { "GulfOfGuinea", (533, 475) },
        { "Alaska", (68, 113) },
        { "Canada", (200, 137) },
    };
}
