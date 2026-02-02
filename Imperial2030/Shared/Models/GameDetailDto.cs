using System.Collections.Generic;

namespace Imperial2030.Shared.Models;

public class GameDetailDto : GameDto
{
    public List<PlayerDto> Players { get; set; } = new List<PlayerDto>();
}
