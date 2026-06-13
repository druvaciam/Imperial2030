using System;
using System.Collections.Generic;

namespace Imperial2030.Shared.Models;

public class MoveUnitRequest
{
    public Guid UnitId { get; set; }
    public string DestinationId { get; set; } = string.Empty;
    public List<Guid>? ConvoyFleetIds { get; set; }
    public Imperial2030.Shared.Models.Nation? BattleTargetNation { get; set; } // If set, resolve battle against this nation after move
    public bool IsHostile { get; set; } = true;
}

public class BattleResponseRequest
{
    public bool IsFight { get; set; }
}
