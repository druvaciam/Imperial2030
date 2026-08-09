namespace Imperial2030.Shared.Models
{
    public class FlagPlacementMetadata
    {
        public string TerritoryId { get; set; } = string.Empty;
        public Nation? OldController { get; set; }
        public Nation? NewController { get; set; }
    }
}
