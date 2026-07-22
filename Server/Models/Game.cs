using Imperial2030.Shared.Models;
using System.ComponentModel.DataAnnotations;

namespace Imperial2030.Server.Models;

public class Game
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(50)]
    public string Name { get; set; } = string.Empty;

    public bool IsPrivate { get; set; } = false;
    
    [MaxLength(10)]
    public string? JoinCode { get; set; }
    
    [Range(2, 6)]
    public int MaxPlayers { get; set; } = 6;

    public GameStatus Status { get; set; } = GameStatus.Lobby;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Nation CurrentTurnNation { get; set; } = Nation.Russia;
    public bool VariantBonusOnlyForTaxIncreases { get; set; } = false;

    // Investor Logic
    public Guid? InvestorCardHolderId { get; set; }
    public bool IsInvestorTurn { get; set; } = false;
    public Guid? ActingPlayerId { get; set; } // If set, this player must take action (e.g. Investor) instead of CurrentTurnNation controller
    public List<Guid> PendingInvestorIds { get; set; } = new List<Guid>(); // Queue for Swiss Bank and Investor card holders

    public ManeuverPhase CurrentManeuverPhase { get; set; } = ManeuverPhase.None;

    // Pending Battle Negotiation State
    public string? PendingBattleTerritoryId { get; set; }
    public Nation? PendingBattleAggressorNation { get; set; }
    
    // Using a list of Nations that must still answer. If a Nation responds Fight, it triggers. 
    // If Peace, they are removed. If empty, Peace prevails.
    public List<Nation> PendingBattleDefenders { get; set; } = new List<Nation>();


    public virtual ICollection<Player> Players { get; set; } = new List<Player>();
    public virtual ICollection<Bond> Bonds { get; set; } = new List<Bond>();
    public virtual ICollection<NationState> NationStates { get; set; } = new List<NationState>();
    public virtual ICollection<TerritoryState> TerritoryStates { get; set; } = new List<TerritoryState>();
    public virtual ICollection<Unit> Units { get; set; } = new List<Unit>();
    public virtual ICollection<GameAction> Actions { get; set; } = new List<GameAction>();

    public void AdvanceTurn()
    {
        var nations = Enum.GetValues(typeof(Nation)).Cast<Nation>().ToList();
        int currentIndex = nations.IndexOf(this.CurrentTurnNation);
        
        for (int i = 1; i <= nations.Count; i++)
        {
            int nextIndex = (currentIndex + i) % nations.Count;
            var nextNation = nations[nextIndex];
            var ns = this.NationStates.FirstOrDefault(n => n.Nation == nextNation);
            
            // Skip nations with no controller
            if (ns != null && ns.ControllerId.HasValue)
            {
                this.CurrentTurnNation = nextNation;
                break;
            }
        }
    }
}
