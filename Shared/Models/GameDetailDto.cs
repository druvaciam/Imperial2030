using System.Collections.Generic;

namespace Imperial2030.Shared.Models;

public class GameDetailDto : GameDto
{
    public List<PlayerDto> Players { get; set; } = new List<PlayerDto>();
    public Nation CurrentTurnNation { get; set; }
    public List<NationStateDto> NationStates { get; set; } = new List<NationStateDto>();
    public List<BondDto> AvailableBonds { get; set; } = new List<BondDto>(); // Bonds in the bank
    public List<TerritoryStateDto> Territories { get; set; } = new List<TerritoryStateDto>();
    public List<Unit> Units { get; set; } = new List<Unit>();
    
    public Guid? InvestorCardHolderId { get; set; }
    public bool IsInvestorTurn { get; set; }
    public Guid? ActingPlayerId { get; set; }

    public ManeuverState? ManeuverState { get; set; }
}

public enum ManeuverPhase
{
    None,
    Fleets,
    Armies,
    Flags
}

public class ManeuverState
{
    public ManeuverPhase Phase { get; set; }
    // We can add "UnitsMoved" set here later if needed, 
    // but storing HasMoved on individual units is likely cleaner.
}
