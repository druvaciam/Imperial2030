using System;
using System.Collections.Generic;

namespace Imperial2030.Shared.Models;

public class GameDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public GameStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public int PlayerCount { get; set; }
    public int MaxPlayers { get; set; } = 6;
    public bool IsFull => PlayerCount >= MaxPlayers;
    public List<string> UserIds { get; set; } = [];
    public string? HostId { get; set; }
}
