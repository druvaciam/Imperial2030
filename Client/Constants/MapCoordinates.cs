using System.Collections.Generic;

namespace Imperial2030.Client.Constants;

public static class MapCoordinates
{
    // Centers for clickable regions and unit placement
    public static readonly Dictionary<string, (double X, double Y)> TerritoryCenters = new()
    {
        // --- NORTH AMERICA ---
        { "Alaska", (68, 113) },
        { "Canada", (200, 137) },
        { "Quebec", (315, 155) },
        { "NewYork", (281, 229) },
        { "Chicago", (225, 215) },
        { "SanFrancisco", (83, 240) },
        { "NewOrleans", (195, 287) },
        { "Mexico", (135, 333) },

        // --- SOUTH AMERICA ---
        { "Colombia", (237, 394) },
        { "Peru", (219, 441) },
        { "Manaus", (309, 443) },
        { "Fortaleza", (395, 433) },
        { "Brasilia", (341, 488) },
        { "RioDeJaneiro", (380, 500) },
        { "Argentina", (280, 539) },

        // --- EUROPE ---
        { "London", (537, 160) },
        { "Paris", (550, 188) },
        { "Switzerland", (580, 210) }, // Added Switzerland
        { "Rome", (588, 235) },
        { "Berlin", (588, 172) },
        { "Ukraine", (646, 213) },


        // --- AFRICA ---
        { "North-Africa", (543, 293) },
        { "Guinea", (508, 405) },
        { "Nigeria", (565, 400) }, // EST
        { "Congo", (600, 480) }, // EST
        { "SouthAfrica", (620, 580) }, // EST
        { "EastAfrica", (650, 450) }, // EST
        { "Egypt", (640, 300) }, // EST

        // --- RUSSIA & ASIA ---
        { "Murmansk", (652, 73) },
        { "Moscow", (685, 166) },
        { "Novosibirsk", (842, 143) },
        { "Kazakhstan", (828, 219) },
        { "Vladivostok", (1035, 195) },
        { "Urumqi", (845, 210) },
        { "Beijing", (957, 226) },
        { "Shanghai", (1000, 280) },
        { "Chongqing", (935, 298) },
        { "Indochina", (950, 380) }, // EST
        { "Indonesia", (1000, 480) }, // EST
        { "Philippines", (1080, 400) }, // EST
        { "Japan", (1050, 210) },
        { "Korea", (999, 238) },
        { "Mongolia", (856, 187) },
        { "Iran", (750, 260) }, // EST
        { "Afghanistan", (775, 247) },
        { "Turkey", (703, 227) },
        { "NearEast", (700, 280) }, // EST

        // --- INDIA ---
        { "NewDelhi", (828, 279) },
        { "Kolkata", (890, 329) },
        { "Mumbai", (823, 349) },
        { "Chennai", (850, 360) },

        // --- OCEANIA ---
        { "Australia", (1050, 600) }, // EST
        { "NewZealand", (1150, 700) }, // EST

        // --- SEAS ---
        { "NorthPacific", (50, 150) }, // Split?
        { "NorthAtlantic", (405, 250) },
        { "SouthAtlantic", (450, 500) }, // EST
        { "CaribbeanSea", (250, 350) }, // EST
        { "MediterraneanSea", (615, 270) },
        { "NorthSea", (560, 140) }, // EST
        { "IndianOcean", (850, 500) }, // EST
        { "ChinaSea", (1000, 350) }, // EST
        { "SeaOfJapan", (1050, 200) }, // EST
        { "SouthPacific", (150, 550) }, // EST
        { "TasmanSea", (1100, 650) }, // EST
    };
}
