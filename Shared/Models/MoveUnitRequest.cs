using System;
using System.Collections.Generic;

namespace Imperial2030.Shared.Models;

public class MoveUnitRequest
{
    public Guid UnitId { get; set; }
    public string DestinationId { get; set; } = string.Empty;
    public List<Guid>? ConvoyFleetIds { get; set; }
    public Imperial2030.Shared.Models.Nation? BattleTargetNation { get; set; } // If set, resolve battle against this nation after move
    // If set (alongside BattleTargetNation), pins the exact unit type to destroy when the target nation has
    // more than one type present. Only GameReplayService ever sets this, sourced from the already-logged
    // Battle action's own DefenderUnitType — never from live play, which leaves auto-resolve's target
    // ambiguous by design (the rules give that choice to the attacking player, not yet exposed in the UI).
    public UnitType? BattleTargetUnitType { get; set; }
    public bool IsHostile { get; set; } = true;
}

public class BattleResponseRequest
{
    public bool IsFight { get; set; }
    public Nation? Nation { get; set; }
}
