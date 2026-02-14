using System.Collections.Generic;

namespace Imperial2030.Shared.Models;

public class ImportRequest
{
    public List<ImportUnit> Units { get; set; } = new();
}

public class ImportUnit
{
    public UnitType UnitType { get; set; }
    public string TerritoryId { get; set; } = string.Empty;
}
