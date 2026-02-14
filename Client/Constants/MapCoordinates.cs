using System.Collections.Generic;

namespace Imperial2030.Client.Constants;

public static class MapCoordinates
{
    // Centers for clickable regions and unit placement
    public static readonly Dictionary<string, (double X, double Y)> TerritoryCenters = new()
    {
        // --- NORTH AMERICA ---
        { "Alaska", (80, 113) },
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
        { "Switzerland", (567, 205) },
        { "Rome", (588, 235) },
        { "Berlin", (588, 172) },
        { "Ukraine", (641, 177) },

        // --- AFRICA ---
        { "North-Africa", (543, 293) },
        { "Guinea", (500, 366) },
        { "Nigeria", (580, 350) },
        { "Congo", (620, 420) },
        { "South-Africa", (632, 527) },
        { "East-Africa", (670, 360) },

        // --- RUSSIA & ASIA ---
        { "Murmansk", (652, 73) },
        { "Moscow", (685, 166) },
        { "Novosibirsk", (842, 143) },
        { "Kazakhstan", (781, 188) },
        { "Vladivostok", (1035, 195) },
        { "Urumqi", (845, 210) },
        { "Beijing", (957, 226) },
        { "Shanghai", (1000, 280) },
        { "Chongqing", (935, 298) },
        { "Indochina", (936, 343) },
        { "Indonesia", (986, 417) },
        { "Philippines", (1023, 374) },
        { "Japan", (1067, 250) },
        { "Korea", (1014, 241) },
        { "Mongolia", (902, 182) },
        { "Iran", (743, 276) },
        { "Afghanistan", (775, 247) },
        { "Turkey", (666, 239) },
        { "NearEast", (697, 290) },

        // --- INDIA ---
        { "NewDelhi", (828, 279) },
        { "Kolkata", (890, 329) },
        { "Mumbai", (823, 349) },
        { "Chennai", (850, 360) },

        // --- OCEANIA ---
        { "Australia", (1080, 536) },
        { "NewZealand", (1150, 585) },

        // --- SEAS ---
        { "NorthPacific", (40, 211) },
        { "NorthAtlantic", (410, 220) },
        { "SouthAtlantic", (460, 490) },
        { "GulfOfGuinea", (545, 485) },
        { "CaribbeanSea", (330, 350) },
        { "MediterraneanSea", (615, 270) },
        { "IndianOcean", (800, 418) },
        { "ChinaSea", (1111, 360) },
        { "SeaOfJapan", (1130, 180) },
        { "SouthPacific", (60, 440) },
        { "TasmanSea", (1020, 588) },
    };
}
