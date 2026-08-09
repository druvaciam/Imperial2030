namespace Imperial2030.Shared.Models
{
    public class HostilityMetadata
    {
        public UnitType UnitType { get; set; }
        public string TerritoryId { get; set; } = string.Empty;
        public bool IsHostile { get; set; }
    }
}
