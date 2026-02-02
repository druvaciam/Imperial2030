using System.Collections.Generic;

namespace Imperial2030.Shared.Models;

public class GameDetailDto : GameDto
{
    public List<PlayerDto> Players { get; set; } = new List<PlayerDto>();
    public Nation CurrentTurnNation { get; set; }
    public List<NationStateDto> NationStates { get; set; } = new List<NationStateDto>();
    public List<BondDto> AvailableBonds { get; set; } = new List<BondDto>(); // Bonds in the bank
}
