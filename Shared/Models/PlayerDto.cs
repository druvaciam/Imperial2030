using System;

namespace Imperial2030.Shared.Models;

public class PlayerDto
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public bool IsHost { get; set; }
    public int Cash { get; set; }
    public bool IsOnline { get; set; }
    public bool IsActiveInGame { get; set; }
    public bool IsBot { get; set; }
    public List<BondDto> Bonds { get; set; } = [];
}
