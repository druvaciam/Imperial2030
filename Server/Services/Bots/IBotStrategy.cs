using Imperial2030.Shared.Models;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Constants;

namespace Imperial2030.Server.Services.Bots;

public class PendingBattle
{
    public string TerritoryId { get; set; } = "";
    public Imperial2030.Shared.Models.Nation AggressorNation { get; set; }
    public List<Imperial2030.Shared.Models.Nation> DefenderNations { get; set; } = new();
}

public interface IBotStrategy
{
    string Name { get; }
    
    // Core decision weighting
    double ScoreRondelSlot(int slot, Game game, NationState ns, Player controller, int factories, int units);

    double GetRondelSelectionWeight(double candidateScore, double highestCandidateScore);
    
    // Specific actions
    Bond? ChooseBondToBuy(Game game, Player actor, List<Nation> controlledNations, List<Bond> availableBonds);
    
    string? ChooseCityForFactory(Game game, Nation nation, List<Territory> validCities);
    
    /// <summary>A whole import planned at once from the current board; the heuristics' planner. Tests and the E2E driver use it directly.</summary>
    List<(UnitType Type, string TerritoryId)> ChooseImports(Game game, NationState ns, int maxImport, List<Territory> homeTerritories);

    /// <summary>
    /// The next unit to import - decided on the board as it is now, with the units already placed this
    /// turn on it and paid for - or null to stop. This is how the bot imports: place, then ask again, up
    /// to <paramref name="remaining"/> more times; the same sequence of questions the RL policy is
    /// trained on.
    /// </summary>
    (UnitType Type, string TerritoryId)? ChooseNextImport(Game game, NationState ns, int remaining, List<Territory> homeTerritories);
    
    double ScoreManeuverDestination(Game game, Unit unit, string destinationId, Player controller);

    /// <summary>
    /// Whether the bot ends the current maneuver phase now, before <paramref name="nextUnit"/> and every
    /// unit after it have moved; they stay where they are. Asked before each unit of the phase.
    /// </summary>
    bool EndsManeuverPhaseEarly(Game game, Unit nextUnit, Player controller);
    
    bool RetreatFromBattle(Game game, PendingBattle battle);
    
    bool DetermineHostility(bool hasEnemy, bool isForeignHome);

    /// <summary>
    /// Decides whether the bot should destroy an enemy factory when it has >= 3 armies on a foreign factory territory.
    /// </summary>
    bool ShouldDestroyFactory(Game game, Nation nation, string territoryId, Player controller);
}
