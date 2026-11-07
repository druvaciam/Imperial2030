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

/// <summary>
/// A rondel space this nation may legally move its marker to now: clockwise only, at most
/// <see cref="RondelData.MaxMoveDistance"/> spaces, and affordable (Imperial-2030-Rules.pdf p.6).
/// <paramref name="Score"/> is what the strategy's own scoring made of it.
/// </summary>
public sealed record RondelCandidate(int Slot, int Distance, int MoveCost, double Score);

public interface IBotStrategy
{
    string Name { get; }

    // Core decision weighting
    double ScoreRondelSlot(int slot, Game game, NationState ns, Player controller, int factories, int units);

    double GetRondelSelectionWeight(double candidateScore, double highestCandidateScore);

    /// <summary>
    /// A last look at the rondel move before it is made: <paramref name="candidates"/> are every space
    /// this nation may legally move to and <paramref name="scoringPick"/> is the one the strategy's own
    /// scoring settled on. Returns a different space to move there instead, or null to keep the pick.
    ///
    /// Asynchronous because a strategy may consult something outside the process to answer; the pick is
    /// already made by then, so a strategy that has nothing to add answers null without waiting, and one
    /// that cannot answer in time keeps the pick as well.
    /// </summary>
    Task<int?> ReconsiderRondelSlotAsync(Game game, NationState ns, Player controller, IReadOnlyList<RondelCandidate> candidates, int scoringPick);
    
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

    // --- Second looks -------------------------------------------------------------------------------
    //
    // Every decision above is made by the strategy's own scoring; each of these is the last look at that
    // decision before it is taken, given the options it could legally have chosen instead. Returning null
    // keeps the decision as it was, which is what the heuristics all do - a strategy that consults
    // something outside the process (the Jev layer) answers here, and when it cannot answer in time, the
    // decision it was asked about still stands.
    //
    // They are on the shared contract, not on one strategy, because the options have to come from the
    // common path: a strategy may value a position differently, never see more of it or more moves in it
    // (.agents/AGENTS.md, "No Bot-Identity Cheating Or Strategy-Exclusive Rules Actions").

    /// <summary>The bond to buy, out of <paramref name="affordable"/>, or null to keep the pick (which may be no purchase).</summary>
    Task<Bond?> ReconsiderBondAsync(Game game, Player actor, IReadOnlyList<Bond> affordable, Bond? pick);

    /// <summary>Which home city gets the factory, out of the ones it may legally be built in (p.7).</summary>
    Task<string?> ReconsiderFactoryCityAsync(Game game, NationState ns, Player controller, IReadOnlyList<Territory> buildable, string pick);

    /// <summary>
    /// The next unit to buy and where to put it, out of the placements the rules allow (p.8), or null to
    /// keep the pick. <paramref name="remaining"/> is how many more this turn may still buy.
    /// </summary>
    Task<(UnitType Type, string TerritoryId)?> ReconsiderImportAsync(Game game, NationState ns, Player controller,
        IReadOnlyList<(UnitType Type, string TerritoryId)> placements, (UnitType Type, string TerritoryId) pick, int remaining);

    /// <summary>
    /// Where this unit goes, out of the regions it may legally reach - staying put among them. Asked once
    /// per unit, as the maneuver moves them one at a time.
    /// </summary>
    Task<string?> ReconsiderManeuverDestinationAsync(Game game, Unit unit, Player controller, IReadOnlyList<string> destinations, string pick);

    /// <summary>
    /// Whether the unit enters (or stands in) <paramref name="territoryId"/> upright - which is what makes
    /// it an occupation and offers battle (p.10) - or lies down. Only asked where both are allowed.
    /// </summary>
    Task<bool?> ReconsiderHostilityAsync(Game game, Unit unit, Player controller, string territoryId, bool pick);

    /// <summary>Whether this nation fights the battle it has been offered, or retreats (p.9).</summary>
    Task<bool?> ReconsiderBattleResponseAsync(Game game, PendingBattle battle, Player controller, bool fightPick);

    /// <summary>Whether to pull down the factory this nation's armies are standing on (p.10).</summary>
    Task<bool?> ReconsiderFactoryDestructionAsync(Game game, Nation nation, string territoryId, Player controller, bool destroyPick);
}
