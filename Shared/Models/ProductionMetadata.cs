using System.Collections.Generic;

namespace Imperial2030.Shared.Models
{
    public class ProductionMetadata
    {
        public int ProducedCount { get; set; }
        public List<ProductionUnitInfo> Units { get; set; } = new();
    }

    public class ProductionUnitInfo
    {
        public UnitType UnitType { get; set; }
        public string TerritoryId { get; set; } = string.Empty;
        public string TerritoryName { get; set; } = string.Empty;
    }
}
