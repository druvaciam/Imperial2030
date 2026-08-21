namespace Imperial2030.Shared.Models
{
    public class ActionMetadata
    {
        public string? TerritoryId { get; set; }
        public string? FromTerritoryId { get; set; }
        public string? ToTerritoryId { get; set; }
        // The moving unit's own IsHostile flag at its ORIGIN territory, captured immediately before this
        // move mutates it. Distinct from IsHostileMove (whether the move ITSELF is hostile — arrival
        // behavior at the destination): this describes the pre-move state of the specific unit being
        // moved, which disambiguates between two otherwise-identical units (same Nation/UnitType/
        // FromTerritory/HasMoved) sitting at the same origin during MoveArmy/MoveFleet replay matching.
        public bool? SourceIsHostile { get; set; }
        public Nation? AggressorNation { get; set; }
        public Nation? DefenderNation { get; set; }
        public UnitType? UnitType { get; set; }
        public UnitType? DefenderUnitType { get; set; }
        public bool? IsHostileMove { get; set; }
        public string? DefendersStr { get; set; }
        public bool? IsResponse { get; set; }
        public string? RespondingNationStr { get; set; }
        // Every territory the unit passed THROUGH, in travel order, excluding origin and destination:
        // rail hops, the territory a convoyed army boarded at, and the sea regions it was carried
        // across. Null or empty for a plain step to an adjacent territory, which has nothing in between.
        //
        // Recorded because the route is NOT derivable from origin and destination after the fact:
        // several routes can connect the same pair, and the carrying fleets are flagged HasConvoyed the
        // moment the move completes, which erases the evidence of which ones did the carrying. Nullable
        // and additive, so action logs written before this field existed still deserialize - they simply
        // carry no route, and consumers fall back to drawing the move direct.
        public List<string>? RouteVia { get; set; }
    }
}
