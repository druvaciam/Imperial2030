using System;
using System.Collections.Generic;

namespace Imperial2030.Shared.Models;

public class MoveUnitRequest
{
    public Guid UnitId { get; set; }
    public string DestinationId { get; set; } = string.Empty;
    public List<Guid>? ConvoyFleetIds { get; set; }
}
