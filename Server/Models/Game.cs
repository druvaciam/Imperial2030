using Imperial2030.Shared.Models;
using System.ComponentModel.DataAnnotations;

namespace Imperial2030.Server.Models;

public class Game
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(50)]
    public string Name { get; set; } = string.Empty;

    public GameStatus Status { get; set; } = GameStatus.Lobby;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Nation CurrentTurnNation { get; set; } = Nation.Russia;

    // Investor Logic
    public Guid? InvestorCardHolderId { get; set; }
    public bool IsInvestorTurn { get; set; } = false;
    public Guid? ActingPlayerId { get; set; } // If set, this player must take action (e.g. Investor) instead of CurrentTurnNation controller

    public ManeuverPhase CurrentManeuverPhase { get; set; } = ManeuverPhase.None;


    public virtual ICollection<Player> Players { get; set; } = new List<Player>();
    public virtual ICollection<Bond> Bonds { get; set; } = new List<Bond>();
    public virtual ICollection<NationState> NationStates { get; set; } = new List<NationState>();
    public virtual ICollection<TerritoryState> TerritoryStates { get; set; } = new List<TerritoryState>();
    public virtual ICollection<Unit> Units { get; set; } = new List<Unit>();
    public virtual ICollection<GameAction> Actions { get; set; } = new List<GameAction>();
}
