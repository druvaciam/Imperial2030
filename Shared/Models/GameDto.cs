using System;
using System.Collections.Generic;

namespace Imperial2030.Shared.Models;

public class GameDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public GameStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public int PlayerCount { get; set; }
    public int MaxPlayers { get; set; } = 6;
    public bool IsPrivate { get; set; } = false;
    public string? JoinCode { get; set; }
    public bool IsFull => PlayerCount >= MaxPlayers;
    public List<string> UserIds { get; set; } = [];
    public string? HostId { get; set; }
    public string? HostName { get; set; }
    public int MaxPower { get; set; }
    public int TurnCount { get; set; }
    public bool VariantBonusOnlyForTaxIncreases { get; set; } = false;
    public string? WinnerName { get; set; }
    public bool IsPaused { get; set; } = false;
    // True when every player is a bot — the case for every imported game (importers never get a real
    // Player row of their own, see ImportGame) as well as bot-vs-bot exhibition games. Since no real
    // human host exists to delete these through the normal host-only path, they're deletable by any
    // signed-in (non-guest) user instead — see GamesController.DeleteGame.
    public bool IsAllBots { get; set; } = false;
}
