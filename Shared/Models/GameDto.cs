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
    /// <summary>
    /// Whether the caller is a player in this game.
    ///
    /// The lobby list is served anonymously, and previously carried `UserIds` - every player's raw
    /// ASP.NET Identity id, for every game, to everyone - together with the host's. The client only ever
    /// compared those against the CALLER's own id, so the questions are answered here instead and no user
    /// id leaves the server.
    /// </summary>
    public bool IsCurrentUserInGame { get; set; }

    /// <summary>Whether the caller hosts this game. See <see cref="IsCurrentUserInGame"/>.</summary>
    public bool IsCurrentUserHost { get; set; }
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
