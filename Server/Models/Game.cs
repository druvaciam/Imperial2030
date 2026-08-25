using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using System.ComponentModel.DataAnnotations;

namespace Imperial2030.Server.Models;

public class Game
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(GameConstants.MaxGameNameLength)]
    public string Name { get; set; } = string.Empty;

    public bool IsPrivate { get; set; } = false;

    [MaxLength(10)]
    public string? JoinCode { get; set; }

    [Range(2, 6)]
    public int MaxPlayers { get; set; } = 6;

    public GameStatus Status { get; set; } = GameStatus.Lobby;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public int TurnCount { get; set; } = 0;
    public Nation CurrentTurnNation { get; set; } = Nation.Russia;
    public bool VariantBonusOnlyForTaxIncreases { get; set; } = false;
    
    [MaxLength(50)]
    public string? WinnerName { get; set; }

    public bool IsPaused { get; set; } = false;

    // Investor Logic
    public Guid? InvestorCardHolderId { get; set; }
    public bool IsInvestorTurn { get; set; } = false;
    public Guid? ActingPlayerId { get; set; } // If set, this player must take action (e.g. Investor) instead of CurrentTurnNation controller
    // A plain list, mapped by EF as a primitive collection — the same shape as PendingBattleDefenders and
    // PendingSwissBankResponders below. It was previously a [NotMapped] accessor over a hand-serialised
    // PendingInvestorIdsJson column whose getter deserialised a FRESH list on every read, so
    // `PendingInvestorIds.Add(x)` compiled, ran, and silently did nothing — while the identical call on
    // its two neighbours worked. Every call site happened to reassign instead, so nothing was broken, but
    // the three are now the same thing and behave the same way.
    public List<Guid> PendingInvestorIds { get; set; } = new List<Guid>();

    public ManeuverPhase CurrentManeuverPhase { get; set; } = ManeuverPhase.None;

    // Pending Battle Negotiation State
    public string? PendingBattleTerritoryId { get; set; }
    public Nation? PendingBattleAggressorNation { get; set; }

    // The specific unit that moved into the territory and triggered this negotiation - so that
    // when a defender fights, the mover (not some other unit of the same nation already sitting
    // in the territory) is the one put at risk.
    public Guid? PendingBattleAggressorUnitId { get; set; }

    // Using a list of Nations that must still answer. If a Nation responds Fight, it triggers.
    // If Peace, they are removed. If empty, Peace prevails.
    public List<Nation> PendingBattleDefenders { get; set; } = new List<Nation>();

    // Swiss Bank Forced Stop
    public int? PendingSwissBankForceTargetSlot { get; set; }
    public Nation? PendingSwissBankForceNation { get; set; }
    public List<Guid> PendingSwissBankResponders { get; set; } = new List<Guid>();

    public virtual ICollection<Player> Players { get; set; } = new List<Player>();
    public virtual ICollection<Bond> Bonds { get; set; } = new List<Bond>();
    public virtual ICollection<NationState> NationStates { get; set; } = new List<NationState>();
    public virtual ICollection<TerritoryState> TerritoryStates { get; set; } = new List<TerritoryState>();
    public virtual ICollection<Unit> Units { get; set; } = new List<Unit>();
    public virtual ICollection<GameAction> Actions { get; set; } = new List<GameAction>();

    public void ResetStateForNewMove(NationState nationState, Action<Unit>? modifyUnitTracker = null)
    {
        nationState.HasMovedThisTurn = true;
        nationState.HasProducedThisTurn = false;
        nationState.HasBuiltThisTurn = false;
        nationState.HasImportedThisTurn = false;

        foreach (var u in Units.Where(u => u.Nation == nationState.Nation))
        {
            u.HasMoved = false;
            u.HasConvoyed = false;
            modifyUnitTracker?.Invoke(u);
        }
    }

    public void AdvanceTurn()
    {
        this.TurnCount++;
        var currentNs = this.NationStates.FirstOrDefault(n => n.Nation == this.CurrentTurnNation);
        if (currentNs != null)
        {
            currentNs.HasBuiltThisTurn = false;
            currentNs.HasMovedThisTurn = false;
            currentNs.HasImportedThisTurn = false;
        }

        foreach (var unit in this.Units.Where(u => u.Nation == this.CurrentTurnNation))
        {
            unit.HasMoved = false;
            unit.HasConvoyed = false;
        }

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

    public int CalculateScore(Guid playerId)
    {
        var player = this.Players.FirstOrDefault(p => p.Id == playerId);
        if (player == null) return 0;

        int score = player.Cash;
        var playerBonds = this.Bonds.Where(b => b.HolderId == playerId).ToList();

        foreach (var bond in playerBonds)
        {
            var nation = this.NationStates.FirstOrDefault(n => n.Nation == bond.Nation);
            if (nation != null)
            {
                int factor = Imperial2030.Shared.Constants.RondelData.GetPowerFactor(nation.Power);
                score += bond.Interest * factor;
            }
        }
        return score;
    }

    public List<Player> GetRankedPlayers()
    {
        var rankedNations = this.NationStates
            .OrderByDescending(ns => ns.Power)
            .Select(ns => ns.Nation)
            .ToList();

        var playerScores = this.Players.ToDictionary(p => p.Id, p => CalculateScore(p.Id));
        var playerCredits = this.Players.ToDictionary(p => p.Id, p =>
        {
            var credits = new Dictionary<Nation, int>();
            foreach (var nation in rankedNations)
            {
                credits[nation] = this.Bonds.Where(b => b.HolderId == p.Id && b.Nation == nation).Sum(b => b.Cost);
            }
            return credits;
        });

        // OrderBy, not List.Sort. Imperial-2030-Rules.pdf p.6's tie-break chain ("the player who has the
        // higher credit sum in the nation with the most power points ... and so on") can still end in an
        // absolute tie, and the comparison below then returns 0. List.Sort is an introsort and therefore
        // UNSTABLE: it may order equal elements differently from one call to the next, so the same
        // finished game could name a different winner on a replay than it did originally — and
        // TestImportFromExportedJson asserts WinnerName matches exactly.
        //
        // OrderBy is documented as stable, so tied players simply keep their roster order. No winner is
        // invented for a case the rulebook leaves open; the result is only made repeatable.
        return this.Players
            .OrderBy(p => p, Comparer<Player>.Create((p1, p2) =>
            {
                int scoreDiff = playerScores[p2.Id].CompareTo(playerScores[p1.Id]);
                if (scoreDiff != 0) return scoreDiff;

                // Tie-breaker: credit sum in nations ranked by power points
                foreach (var nation in rankedNations)
                {
                    int creditDiff = playerCredits[p2.Id][nation].CompareTo(playerCredits[p1.Id][nation]);
                    if (creditDiff != 0) return creditDiff;
                }

                return 0; // Absolute tie — left to the stable order rather than broken arbitrarily.
            }))
            .ToList();
    }
}
