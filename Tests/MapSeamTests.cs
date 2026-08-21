using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using Xunit;

namespace Imperial2030.Tests;

/// <summary>
/// The map image is a flat rectangle but the world is a sphere: the Pacific regions on its west edge
/// are adjacent to the Asian ones on its east edge. These guard the connectivity data those
/// cross-seam moves depend on — GameMap draws such a move as two segments leaving opposite borders
/// rather than one line back across the map, and that split is driven by exactly these pairs.
///
/// Only the connectivity is asserted here, not the on-screen coordinates: those live in
/// Client/Constants/MapCoordinates.cs and Tests deliberately does not reference the Client project.
///
/// Source: Imperial-2030-Rules.pdf, "Maneuver" — "The earth is a sphere".
/// </summary>
public class MapSeamTests
{
    // Mirrors GameMap.SeamPairs. A pair added there must exist in the connectivity data too,
    // otherwise the map would draw a crossing for a move the rules do not actually allow.
    public static IEnumerable<object[]> SeamPairs() => new[]
    {
        new object[] { "NorthPacific", "SeaOfJapan" },
        new object[] { "NorthPacific", "ChinaSea" },
        new object[] { "SouthPacific", "ChinaSea" },
        new object[] { "SouthPacific", "TasmanSea" },
    };

    [Theory]
    [MemberData(nameof(SeamPairs))]
    public void SeamRegionsAreAdjacentInBothDirections(string west, string east)
    {
        Assert.Contains(east, MapConnectivity.GetNeighbors(west, isFleet: true));
        Assert.Contains(west, MapConnectivity.GetNeighbors(east, isFleet: true));
    }

    [Fact]
    public void NorthAndSouthPacificAreOrdinaryNeighboursNotASeamCrossing()
    {
        // The rules wrap east-west only: "there are no such connections on the map from North to
        // South". North and South Pacific are adjacent, but down the same edge — so this pair must
        // NOT appear in the seam list, or the map would split a perfectly ordinary move in two.
        Assert.Contains("SouthPacific", MapConnectivity.GetNeighbors("NorthPacific", isFleet: true));

        var seam = SeamPairs().Select(p => ((string)p[0], (string)p[1])).ToList();
        Assert.DoesNotContain(("NorthPacific", "SouthPacific"), seam);
        Assert.DoesNotContain(("SouthPacific", "NorthPacific"), seam);
    }

    [Fact]
    public void ConvoyableSeamCrossingExistsForArmies()
    {
        // An army cannot enter a sea region on its own, but it can be convoyed across one — which is
        // why a move path has to be able to run through sea regions and over the seam. Japan ->
        // Sea of Japan -> North Pacific -> Alaska is such a route, and it crosses the seam midway.
        //
        // The carrying legs are checked fleet-side (sea-to-sea), the disembark land-side: GetNeighbors
        // filters by the mover's own type, and a fleet can never end on Alaska even though an army
        // convoyed there can.
        Assert.Contains("SeaOfJapan", MapConnectivity.GetNeighbors("Japan", isFleet: true));
        Assert.Contains("NorthPacific", MapConnectivity.GetNeighbors("SeaOfJapan", isFleet: true));
        Assert.Contains("Alaska", MapConnectivity.GetNeighbors("NorthPacific", isFleet: false));

        // ...and the army itself cannot step into that sea region unaided.
        Assert.DoesNotContain("SeaOfJapan", MapConnectivity.GetNeighbors("Japan", isFleet: false));
    }

    [Fact]
    public void InlandOriginNeedsACoastalEmbarkationPointBeforeAnyConvoy()
    {
        // A convoy starting inland has a leg before it ever reaches water: the army rails to the coast
        // and boards there. Moscow -> Alaska is the case that exposed this - the drawn path ran straight
        // from Moscow to the Sea of Japan, which no army can do, because the reconstruction reported
        // only the carrying fleets' sea regions and dropped the territory the army actually boarded at.
        //
        // Moscow touches no sea at all, so an embarkation point is not optional here.
        var seaRegionIds = TerritoryData.AllTerritories
            .Where(t => t.Type == TerritoryType.Sea)
            .Select(t => t.Id)
            .ToHashSet();
        Assert.DoesNotContain(MapConnectivity.GetNeighbors("Moscow", isFleet: true), seaRegionIds.Contains);

        // Vladivostok is the coastal territory that reaches it, and it is rail-reachable from Moscow
        // through Russia's own home provinces.
        Assert.Contains("SeaOfJapan", MapConnectivity.GetNeighbors("Vladivostok", isFleet: true));
        Assert.Contains("Novosibirsk", MapConnectivity.GetNeighbors("Moscow", isFleet: false));
        Assert.Contains("Vladivostok", MapConnectivity.GetNeighbors("Novosibirsk", isFleet: false));

        // ...and from there the sea route to Alaska crosses the seam, so this one move exercises both
        // the embarkation leg and the border split.
        Assert.Contains("NorthPacific", MapConnectivity.GetNeighbors("SeaOfJapan", isFleet: true));
        Assert.Contains("Alaska", MapConnectivity.GetNeighbors("NorthPacific", isFleet: false));
    }
}
