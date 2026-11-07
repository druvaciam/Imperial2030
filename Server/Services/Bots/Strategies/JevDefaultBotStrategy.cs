using Imperial2030.Server.Models;
using Imperial2030.Server.Services.TypeSafe;
using Imperial2030.Shared.Models;

namespace Imperial2030.Server.Services.Bots.Strategies;

/// <summary>
/// The plain heuristic bot with the same Jev second opinion the RL bot gets, as bot type "Jev-Default".
///
/// It exists to answer a question the RL comparison cannot: layered over a model with 40M training steps
/// behind it, advice that changes nothing looks the same as advice that is worthless. Layered over a bot
/// that plays by a handful of hand-written rules, useful advice should show. If it cannot lift this one,
/// it is not adding game understanding.
/// </summary>
public class JevDefaultBotStrategy : DefaultBotStrategy
{
    public const string BotType = JevAdviser.BotTypePrefix + "Default";

    private readonly JevAdviser _adviser;

    public override string Name => BotType;

    public JevDefaultBotStrategy(TypeSafeClient? typeSafe = null, ILogger? logger = null)
        => _adviser = new JevAdviser(typeSafe, logger);

    public override Task<int?> ReconsiderRondelSlotAsync(Game game, NationState ns, Player controller, IReadOnlyList<RondelCandidate> candidates, int scoringPick)
        => _adviser.ReconsiderRondelSlotAsync(game, ns, controller, candidates, scoringPick);

    public override Task<Bond?> ReconsiderBondAsync(Game game, Player actor, IReadOnlyList<Bond> affordable, Bond? pick)
        => _adviser.ReconsiderBondAsync(game, actor, affordable, pick);

    public override Task<string?> ReconsiderFactoryCityAsync(Game game, NationState ns, Player controller, IReadOnlyList<Territory> buildable, string pick)
        => _adviser.ReconsiderFactoryCityAsync(game, ns, controller, buildable, pick);

    public override Task<(UnitType Type, string TerritoryId)?> ReconsiderImportAsync(Game game, NationState ns, Player controller,
        IReadOnlyList<(UnitType Type, string TerritoryId)> placements, (UnitType Type, string TerritoryId) pick, int remaining)
        => _adviser.ReconsiderImportAsync(game, ns, controller, placements, pick, remaining);

    public override Task<string?> ReconsiderManeuverDestinationAsync(Game game, Unit unit, Player controller, IReadOnlyList<string> destinations, string pick)
        => _adviser.ReconsiderManeuverDestinationAsync(game, unit, controller, destinations, pick);

    public override Task<bool?> ReconsiderHostilityAsync(Game game, Unit unit, Player controller, string territoryId, bool pick)
        => _adviser.ReconsiderHostilityAsync(game, unit, controller, territoryId, pick);

    public override Task<bool?> ReconsiderBattleResponseAsync(Game game, PendingBattle battle, Player controller, bool fightPick)
        => _adviser.ReconsiderBattleResponseAsync(game, battle, controller, fightPick);

    public override Task<bool?> ReconsiderFactoryDestructionAsync(Game game, Nation nation, string territoryId, Player controller, bool destroyPick)
        => _adviser.ReconsiderFactoryDestructionAsync(game, nation, territoryId, controller, destroyPick);
}
