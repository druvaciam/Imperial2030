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
    }
}
