using System;
using System.Collections.Generic;

namespace Imperial2030.Shared.Models;

public class DestroyFactoryRequest
{
    public string TerritoryId { get; set; } = string.Empty;
    public List<Guid> UnitIds { get; set; } = new();
}
