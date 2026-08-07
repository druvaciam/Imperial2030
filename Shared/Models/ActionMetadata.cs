namespace Imperial2030.Shared.Models
{
    public class ActionMetadata
    {
        public string? TerritoryId { get; set; }
        public string? FromTerritoryId { get; set; }
        public string? ToTerritoryId { get; set; }
        public Nation? AggressorNation { get; set; }
        public Nation? DefenderNation { get; set; }
        public UnitType? UnitType { get; set; }
        public UnitType? DefenderUnitType { get; set; }
    }
}
