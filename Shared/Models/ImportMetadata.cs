using System.Collections.Generic;

namespace Imperial2030.Shared.Models
{
    public class ImportMetadata
    {
        public int ImportedCount { get; set; }
        public List<ImportUnitInfo> Units { get; set; } = new();
    }

    public class ImportUnitInfo
    {
        public UnitType UnitType { get; set; }
        public string TerritoryId { get; set; } = string.Empty;
        public string TerritoryName { get; set; } = string.Empty;
    }
}
