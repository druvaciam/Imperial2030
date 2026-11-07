using Imperial2030.Server.Models;
using Imperial2030.Server.Services.TypeSafe;
using Imperial2030.Shared.Models;

namespace Imperial2030.Server.Services.Bots.Strategies;

/// <summary>
/// A trained RL model with a Jev second opinion on every decision: the model plays as it always did, and
/// before each decision is taken <see cref="JevAdviser"/> is asked the same question over the same legal
/// options. Registered as bot type "Jev-&lt;model&gt;" (for example "Jev-RL-5"), so the model underneath is
/// part of the bot's identity rather than a hidden setting.
///
/// Inference only, and never worse off than the model it wraps: training never reaches the adviser, and
/// every failure - no API key, a timeout, a refused request, an answer naming no legal option - leaves
/// the model's own decision standing. See jev.info.txt for what it has and has not been shown to do.
/// </summary>
public class JevRlBotStrategy : RLBotStrategy
{
    /// <inheritdoc cref="JevAdviser.BotTypePrefix"/>
    public const string BotTypePrefix = JevAdviser.BotTypePrefix;

    /// <inheritdoc cref="JevAdviser.RondelQuestionId"/>
    public const string RondelQuestionId = JevAdviser.RondelQuestionId;
    public const string BondQuestionId = JevAdviser.BondQuestionId;
    public const string FactoryCityQuestionId = JevAdviser.FactoryCityQuestionId;
    public const string ImportQuestionId = JevAdviser.ImportQuestionId;
    public const string ManeuverQuestionId = JevAdviser.ManeuverQuestionId;
    public const string HostilityQuestionId = JevAdviser.HostilityQuestionId;
    public const string BattleQuestionId = JevAdviser.BattleQuestionId;
    public const string FactoryDestructionQuestionId = JevAdviser.FactoryDestructionQuestionId;

    private readonly JevAdviser _adviser;
    private readonly string _name;

    public override string Name => _name;

    /// <inheritdoc cref="JevAdviser.WrappedBotType"/>
    public static string ModelBotType(string jevBotType) => JevAdviser.WrappedBotType(jevBotType);

    /// <inheritdoc cref="JevAdviser.IsJevBotType"/>
    public static bool IsJevBotType(string? botType) => JevAdviser.IsJevBotType(botType);

    public double MinimumConfidence => _adviser.MinimumConfidence;

    /// <param name="botType">The full bot type, "Jev-" and the model's own type.</param>
    /// <param name="typeSafe">Null, or an unconfigured client, means every decision is the model's own.</param>
    public JevRlBotStrategy(string botType = BotTypePrefix + "RL", TypeSafeClient? typeSafe = null, ILogger? logger = null)
        : this(botType, ModelBotType(botType), typeSafe, logger)
    {
    }

    /// <param name="modelBotType">
    /// The model to load, when it is not the one the bot type names - a harness that wraps a model under
    /// a name of its own, so <see cref="BotService.GetStrategy"/> hands out its instance rather than
    /// building a fresh one.
    /// </param>
    protected JevRlBotStrategy(string botType, string modelBotType, TypeSafeClient? typeSafe, ILogger? logger = null)
        : this(botType, modelBotType, new JevAdviser(typeSafe, logger), logger)
    {
    }

    /// <param name="adviser">
    /// The adviser to consult. Takeable from outside so a measurement harness can hand in one that
    /// records every answer, or answers from a transcript instead of the network.
    /// </param>
    protected JevRlBotStrategy(string botType, string modelBotType, JevAdviser adviser, ILogger? logger = null)
        : base(modelBotType, logger)
    {
        _name = botType;
        _adviser = adviser;
    }

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
