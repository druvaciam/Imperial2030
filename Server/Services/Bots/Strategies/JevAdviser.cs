using Imperial2030.Server.Helpers;
using Imperial2030.Server.Models;
using Imperial2030.Server.Services.TypeSafe;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;

namespace Imperial2030.Server.Services.Bots.Strategies;

/// <summary>
/// The second opinion itself: it builds a question about a decision a bot has already made, asks Jev
/// (TypeSafe's System One model), and hands back an answer the bot may take - or null, which leaves the
/// bot's own decision standing.
///
/// A component rather than a base class, because what it does is the same whichever bot holds it. A
/// trained model holds one (<see cref="JevRlBotStrategy"/>) and so does the plain heuristic bot
/// (<see cref="JevDefaultBotStrategy"/>): layering the same adviser over a weak bot and a strong one is
/// how you find out whether the advice is worth anything, or whether the bot underneath was already
/// doing the work.
///
/// Three rules hold for every question. The options come from the shared bot path, never from here, so a
/// layered bot never sees a move the others cannot make (.agents/AGENTS.md, "No Bot-Identity Cheating").
/// An answer naming something that was not offered is dropped. And an answer Jev is less sure of than
/// <see cref="MinimumConfidence"/> is dropped too, leaving the decision as the bot made it.
/// </summary>
public class JevAdviser
{
    /// <summary>What a bot type has to start with to get this layer, followed by the bot it wraps.</summary>
    public const string BotTypePrefix = "Jev-";

    private readonly TypeSafeClient? _typeSafe;
    private readonly ILogger _logger;

    /// <param name="typeSafe">Null, or an unconfigured client, means every decision is the bot's own.</param>
    public JevAdviser(TypeSafeClient? typeSafe, ILogger? logger = null)
    {
        _typeSafe = typeSafe;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    /// <summary>The bot type underneath "Jev-RL-5" or "Jev-Default", i.e. "RL-5" or "Default".</summary>
    public static string WrappedBotType(string jevBotType) => jevBotType.StartsWith(BotTypePrefix, StringComparison.OrdinalIgnoreCase)
        ? jevBotType[BotTypePrefix.Length..]
        : jevBotType;

    public static bool IsJevBotType(string? botType) => botType != null
        && botType.StartsWith(BotTypePrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// How concentrated Jev's answer has to be before it replaces the bot's own decision. Configured with
    /// TypeSafe:MinimumConfidence; the default is <see cref="TypeSafeClient.DefaultMinimumConfidence"/>.
    /// </summary>
    public double MinimumConfidence => _typeSafe?.MinimumConfidence ?? TypeSafeClient.DefaultMinimumConfidence;

    /// <summary>Whether to ask about every decision rather than only the ones that decide something.</summary>
    public bool AskOnEveryMove => _typeSafe?.AskOnEveryMove ?? false;

    /// <summary>Training decides through the action mask and must never wait on an outside service.</summary>
    private static bool IsTraining => RLBotStrategy.IsTraining;

    public async Task<int?> ReconsiderRondelSlotAsync(Game game, NationState ns, Player controller, IReadOnlyList<RondelCandidate> candidates, int scoringPick)
    {
        if (_typeSafe == null || !_typeSafe.IsConfigured || !_typeSafe.Asks(RondelQuestionId)) return null;

        // Training decides through the training server's action mask and must not wait on an outside
        // service; the first placement of a marker is not a move between spaces (p.6) and has no cost to
        // weigh, so there is nothing here to second-guess either.
        if (IsTraining || !ns.RondelPosition.HasValue) return null;

        var options = ReconsiderableOptions(candidates);
        if (options.Count < 2) return null;

        // Nothing to ask when the model already holds the one space that can do anything: every other
        // option's action is dead from this position (RondelOutlookHelper, the same test the training
        // state vector shows the policy), so an override could only make the turn worse. Saves the call.
        bool modelPickWorks = !RondelOutlookHelper.IsWastedRondelSpace(game, ns, scoringPick);
        if (modelPickWorks && options.Values.Count(option => !RondelOutlookHelper.IsWastedRondelSpace(game, ns, option.Slot)) < 2)
        {
            return null;
        }

        // And nothing worth asking when the model's move already looks sound. Over 84 head-to-head games
        // against the model it wraps, second-guessing every move came out level - the layer earns its
        // place on the moves the code can see are questionable, not on the close calls where the policy
        // has 40M training steps of judgement and Jev has one look at the board.
        if (!AskOnEveryMove && !RondelOutlookHelper.MoveLooksQuestionable(game, ns, scoringPick, options.Values.Select(option => option.Slot)))
        {
            return null;
        }

        var criteria = new Dictionary<string, object?>();
        foreach (var option in options)
        {
            criteria[option.Key] = DescribeSpace(game, ns, controller, option.Value);
        }

        // Null when the policy's own space was dropped as the dearer of two identical ones: the question
        // says so, rather than naming an option that is not there.
        string? policysOwnOption = options
            .Where(entry => entry.Value.Slot == scoringPick)
            .Select(entry => entry.Key)
            .FirstOrDefault();

        var question = new ChoiceQuestion(
            new Dictionary<string, object?>
            {
                ["question"] = $"Which space should {ns.Nation} move its rondel marker to?",
                ["how_to_decide"] = new[]
                {
                    "The marker only moves clockwise, and the space it lands on is the action the nation takes this turn.",
                    "Every space it passes over on the way is an action given up: landing one space short of a valuable action can be worth more than a distant one.",
                    "A player's score is their cash plus, for each bond they hold, its interest times the power factor of that bond's nation, so raising the power of a nation whose bonds you hold and keeping cash both count.",
                    "Each option states what that action would actually produce for this nation right now; prefer the largest real gain over a speculative one.",
                    "Military units are not free to keep: every army and fleet costs the treasury 1 million at every Taxation, and units with nothing to take do nothing but lower that nation's income.",
                    "Power only pays when it crosses the next multiple of five, because the power factor is the power track read in fives; the Taxation option states what crossing it would be worth here."
                },
                ["your_own_policy_chose"] = policysOwnOption ?? $"{RondelData.GetSlotName(scoringPick)} - not offered here, because the same action is available for less",
                ["when_to_keep_it"] = "Keep the policy's space unless another option is clearly better for this nation now."
            },
            criteria);

        var answer = await _typeSafe.AskChoiceAsync(BuildState(game, ns, controller), RondelQuestionId, question);
        if (answer == null) return null;

        OnAnswered(game, ns, controller, options, scoringPick, answer);

        if (!options.TryGetValue(answer.Choice, out var chosen))
        {
            _logger.LogWarning("Jev named a space that was not offered for {Nation}: {Choice}", ns.Nation, answer.Choice);
            return null;
        }

        if (answer.Confidence < MinimumConfidence)
        {
            _logger.LogInformation("Jev was {Confidence:0.00} confident about {Nation}'s move, keeping the policy's space",
                                   answer.Confidence, ns.Nation);
            return null;
        }

        return chosen.Slot;
    }

    /// <summary>
    /// Every answer, before this class decides what to do with it: the options as they were asked, the
    /// space the model had picked, and what came back. Nothing here by default - it exists so the
    /// measurement harness can score answers against the position they were asked about without
    /// rebuilding the question, and so a later gate can be tried on recorded data rather than live play.
    /// </summary>
    protected virtual void OnAnswered(Game game, NationState ns, Player controller,
                                      IReadOnlyDictionary<string, RondelCandidate> options, int scoringPick, ChoiceAnswer answer)
    {
    }

    /// <summary>
    /// The spaces worth asking about, keyed by the name Jev answers with. Every legal space is offered
    /// except one whose identical twin - Production and Maneuver each appear on two spaces (p.6) - is
    /// also legal and costs less: that one buys the same action for more money, whatever else is true of
    /// the position.
    /// </summary>
    internal static Dictionary<string, RondelCandidate> ReconsiderableOptions(IReadOnlyList<RondelCandidate> candidates)
    {
        var options = new Dictionary<string, RondelCandidate>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            int? twin = TwinSlot(candidate.Slot);
            if (twin != null && candidates.FirstOrDefault(other => other.Slot == twin.Value) is { } twinCandidate
                && twinCandidate.MoveCost < candidate.MoveCost)
            {
                continue;
            }

            options[OptionName(candidate)] = candidate;
        }

        return options;
    }

    /// <summary>
    /// What Jev answers with. Two spaces can carry the same action (Production and Maneuver each appear
    /// twice), so the distance tells them apart - and it is what the rulebook counts anyway (p.6).
    /// </summary>
    public static string OptionName(RondelCandidate candidate)
        => $"{RondelData.GetSlotName(candidate.Slot)} ({candidate.Distance} space{(candidate.Distance == 1 ? "" : "s")} ahead)";

    private static int? TwinSlot(int slot) => slot switch
    {
        RondelData.ProductionSlot1 => RondelData.ProductionSlot2,
        RondelData.ProductionSlot2 => RondelData.ProductionSlot1,
        RondelData.ManeuverSlot1 => RondelData.ManeuverSlot2,
        RondelData.ManeuverSlot2 => RondelData.ManeuverSlot1,
        _ => null
    };

    /// <summary>What landing on this space would actually do for this nation, from the position as it is.</summary>
    private string DescribeSpace(Game game, NationState ns, Player controller, RondelCandidate candidate)
    {
        string spaces = $"{candidate.Distance} space{(candidate.Distance == 1 ? "" : "s")} ahead";
        string cost = candidate.MoveCost == 0
            ? $"{spaces}, free"
            : $"{spaces}, costs the government {candidate.MoveCost}m of its own {controller.Cash}m";

        switch (candidate.Slot)
        {
            case RondelData.TaxationSlot:
            {
                var preview = TaxationHelper.PreviewTaxation(game, ns);
                return $"Taxation. {cost}. Tax revenue {preview.TotalTaxRevenue}m against {preview.SoldiersPay}m soldiers' pay, "
                     + $"power {ns.Power} + {preview.ExpectedPowerGain}, treasury {ns.Treasury}m "
                     + $"{(preview.ExpectedTreasuryGain >= 0 ? "+" : "-")}{Math.Abs(preview.ExpectedTreasuryGain)}m, "
                     + $"and {preview.ExpectedBonus}m of success bonus to the government's own cash. "
                     + PowerArithmetic(game, ns, controller, preview.ExpectedPowerGain);
            }
            case RondelData.FactorySlot:
            {
                int buildable = TerritoryData.AllTerritories.Count(t => t.Nation == ns.Nation && t.CityType != CityType.None
                    && !game.TerritoryStates.Any(ts => ts.TerritoryId == t.Id && ts.HasFactory)
                    && !game.Units.Any(u => u.TerritoryId == t.Id && u.UnitType == UnitType.Army && u.Nation != ns.Nation && u.IsHostile));
                return $"Factory. {cost}. Builds one factory for {GameConstants.FactoryCost}m from a treasury of {ns.Treasury}m; "
                     + $"{buildable} home cities could take one, and the nation has {TaxationHelper.CountUnblockedFactories(game, ns.Nation)} unblocked factories now.";
            }
            case RondelData.ProductionSlot1:
            case RondelData.ProductionSlot2:
            {
                int built = RondelOutlookHelper.EstimateProductionYield(game, ns.Nation);
                int units = game.Units.Count(u => u.Nation == ns.Nation);
                return $"Production. {cost}. Every unblocked factory builds a unit for free: {built} units this turn. "
                     + $"{UnitStanding(game, ns)} Each unit costs the treasury 1m of soldiers' pay at every Taxation, "
                     + $"so the {units} it has already cost it {units}m a Taxation whether they are doing anything or not, "
                     + "and a new unit does nothing at all until a Maneuver moves it out of its home province.";
            }
            case RondelData.ManeuverSlot1:
            case RondelData.ManeuverSlot2:
            {
                int armies = game.Units.Count(u => u.Nation == ns.Nation && u.UnitType == UnitType.Army);
                int fleets = game.Units.Count(u => u.Nation == ns.Nation && u.UnitType == UnitType.Fleet);
                int flags = game.TerritoryStates.Count(ts => ts.Controller == ns.Nation);
                int occupied = TerritoryData.AllTerritories.Count(t => t.Nation == ns.Nation
                    && game.Units.Any(u => u.TerritoryId == t.Id && u.UnitType == UnitType.Army && u.Nation != ns.Nation && u.IsHostile));
                return $"Maneuver. {cost}. Moves {fleets} fleets and then {armies} armies, and places a flag in each region it ends up "
                     + $"holding alone - never in a home province, anyone's. {FlagsWithinReach(game, ns)} regions next to its units "
                     + $"could take a flag of this nation, it holds {flags} of its {TaxationRules.MaxFlagsPerNation} flags, and each flag is worth "
                     + $"{TaxationRules.RevenuePerFlag}m of tax revenue. {occupied} of its own home provinces are occupied by hostile "
                     + "armies, which blocks their factories until an army drives them out.";
            }
            case RondelData.InvestorSlot:
            {
                var preview = InvestorHelper.PreviewInterestPayment(game, ns, controller);
                int owedToController = game.Bonds.Where(b => b.Nation == ns.Nation && b.HolderId == controller.Id).Sum(b => b.Interest);
                int owedToOthers = game.Bonds.Where(b => b.Nation == ns.Nation && b.HolderId != null && b.HolderId != controller.Id).Sum(b => b.Interest);
                string outcome = preview.NetControllerCashDelta >= 0
                    ? $"the government would receive {preview.NetControllerCashDelta}m"
                    : $"the government would pay {-preview.NetControllerCashDelta}m out of its own cash to cover what the treasury cannot";
                return $"Investor. {cost}. The nation pays bond interest from a treasury of {ns.Treasury}m: {owedToOthers}m owed to other players "
                     + $"and {owedToController}m to this government, so {outcome}"
                     + $"{(preview.WillGetFullOwnInterest ? "" : " and it would not be paid its own interest in full")}. "
                     + "Then the player holding the investor card takes 2m from the bank and may take a new bond in any nation, or trade one they hold "
                     + "up for a larger one in the same nation; players holding a Swiss Bank may invest as well, without the 2m.";
            }
            case RondelData.ImportSlot:
            {
                int units = game.Units.Count(u => u.Nation == ns.Nation);
                return $"Import. {cost}. Buys up to {GameConstants.MaxImportUnits} units at {GameConstants.ImportUnitCost}m each from a treasury of {ns.Treasury}m, "
                     + $"placed in its own home provinces that no hostile army occupies (fleets only in harbour cities); "
                     + $"the nation has {units} units on the board.";
            }
            default:
                return $"{RondelData.GetSlotName(candidate.Slot)}. {cost}.";
        }
    }

    /// <summary>
    /// What the power this taxation would add is actually worth, in points. A player's score is their
    /// cash plus, for each bond they hold, its interest times the power factor of that bond's nation
    /// (Game.CalculateScore), and the factor is the power track read in fives (p.6) - so power only pays
    /// when it crosses the next five, and then it pays every bondholder of this nation at once. Nothing
    /// else in the question says how close this nation is to that line or what crossing it is worth.
    /// </summary>
    private static string PowerArithmetic(Game game, NationState ns, Player controller, int powerGain)
    {
        int projected = Math.Min(GameConstants.MaxPowerPoints, ns.Power + powerGain);
        int factorNow = RondelData.GetPowerFactor(ns.Power);
        int factorAfter = RondelData.GetPowerFactor(projected);
        int ownInterest = game.Bonds.Where(b => b.Nation == ns.Nation && b.HolderId == controller.Id).Sum(b => b.Interest);
        int rivalInterest = game.Bonds.Where(b => b.Nation == ns.Nation && b.HolderId != null && b.HolderId != controller.Id).Sum(b => b.Interest);
        int toNextFactor = ((ns.Power / RondelData.PowerPerFactorPoint) + 1) * RondelData.PowerPerFactorPoint - ns.Power;

        string crossing = factorAfter > factorNow
            ? $"That crosses into power factor {factorAfter}, worth {(factorAfter - factorNow) * ownInterest} points to this government "
              + $"and {(factorAfter - factorNow) * rivalInterest} to the other holders of its bonds, immediately."
            : $"That is still power factor {factorNow}: {toNextFactor} more power points would reach the next one, "
              + $"which is worth {ownInterest} points to this government and {rivalInterest} to the other holders of its bonds.";

        string ending = projected >= GameConstants.MaxPowerPoints
            ? $" Reaching {GameConstants.MaxPowerPoints} power ends the game at once, scored as it stands."
            : "";

        return crossing + ending;
    }

    /// <summary>
    /// The board itself, written out: where this nation's units stand, what its home provinces hold, what
    /// it has flagged, and every foreign unit within reach of it, with what each region is next to.
    ///
    /// Without this a question about a unit was a question about a name: the trained policy reads a map
    /// of every region, unit and flag, and Jev was being handed a line of totals. Everything here is
    /// public - what any player at the table sees by looking at it.
    /// </summary>
    internal static Dictionary<string, object?> BuildBoard(Game game, NationState ns)
    {
        var ownUnits = game.Units.Where(u => u.Nation == ns.Nation).ToList();

        string Region(string id) => TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == id)?.Name ?? id;

        string Occupants(string id)
        {
            var units = game.Units.Where(u => u.TerritoryId == id).GroupBy(u => (u.Nation, u.UnitType))
                .Select(group => $"{group.Count()} {group.Key.Nation} {group.Key.UnitType.ToString().ToLowerInvariant()}"
                               + (group.Count() == 1 ? "" : "s")
                               + (group.Key.UnitType == UnitType.Army && group.All(u => u.IsHostile) ? " (standing upright)" : ""))
                .ToList();
            return units.Count == 0 ? "empty" : string.Join(", ", units);
        }

        string Neighbours(string id, bool asFleet)
            => string.Join("; ", MapConnectivity.GetNeighbors(id, asFleet)
                .Select(neighbour => $"{Region(neighbour)} ({Occupants(neighbour)})"));

        // Its own units, and which of them this maneuver has already moved - a unit is asked about one at
        // a time, so without this each question is answered as if it were the only unit on the board.
        var units = ownUnits.Select(unit =>
            $"{unit.UnitType.ToString().ToLowerInvariant()} in {Region(unit.TerritoryId)}"
            + (unit.HasMoved ? " (already moved this maneuver)" : " (still to move)")
            + (unit.UnitType == UnitType.Army && unit.IsHostile ? ", standing upright" : "")).ToList();

        var homes = TerritoryData.AllTerritories.Where(t => t.Nation == ns.Nation).Select(home =>
        {
            var state = game.TerritoryStates.FirstOrDefault(ts => ts.TerritoryId == home.Id);
            return $"{home.Name}: {(state?.HasFactory == true ? (home.CityType == CityType.LightBlue ? "shipyard" : "armament facility") : "no factory")}"
                 + $", {Occupants(home.Id)}";
        }).ToList();

        var flags = game.TerritoryStates.Where(ts => ts.Controller == ns.Nation)
            .Select(ts => Region(ts.TerritoryId)).OrderBy(name => name).ToList();

        // Every foreign unit this nation can see from where it stands: in or next to a region of its own.
        var watched = ownUnits.Select(u => u.TerritoryId)
            .Concat(ownUnits.SelectMany(u => MapConnectivity.GetNeighbors(u.TerritoryId, u.UnitType == UnitType.Fleet)))
            .Concat(TerritoryData.AllTerritories.Where(t => t.Nation == ns.Nation).Select(t => t.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var foreign = game.Units.Where(u => u.Nation != ns.Nation && watched.Contains(u.TerritoryId))
            .GroupBy(u => u.TerritoryId)
            .Select(group => $"{Region(group.Key)}: {Occupants(group.Key)}")
            .OrderBy(line => line).ToList();

        var rivalFlags = game.NationStates.Where(other => other.Nation != ns.Nation)
            .ToDictionary(other => other.Nation.ToString(),
                          other => game.TerritoryStates.Count(ts => ts.Controller == other.Nation));

        return new Dictionary<string, object?>
        {
            ["this_nations_units"] = units,
            ["its_home_provinces"] = homes,
            ["regions_it_has_flagged"] = flags.Count == 0 ? new List<string> { "none" } : flags,
            ["foreign_units_it_can_see"] = foreign.Count == 0 ? new List<string> { "none within reach" } : foreign,
            ["flags_held_by_the_other_nations"] = rivalFlags,
            // Keyed by region, not by unit: several units share a region, and what they are next to is
            // the same for all of them.
            ["what_the_regions_it_stands_in_are_next_to"] = ownUnits
                .GroupBy(unit => (unit.TerritoryId, AsFleet: unit.UnitType == UnitType.Fleet))
                .ToDictionary(group => $"{Region(group.Key.TerritoryId)}{(group.Key.AsFleet ? " (by sea)" : "")}",
                              group => (object?)Neighbours(group.Key.TerritoryId, group.Key.AsFleet))
        };
    }

    /// <summary>How many more units the nation may have at all: only so many pieces exist for it.</summary>
    private static string UnitStanding(Game game, NationState ns)
    {
        int armies = game.Units.Count(u => u.Nation == ns.Nation && u.UnitType == UnitType.Army);
        int fleets = game.Units.Count(u => u.Nation == ns.Nation && u.UnitType == UnitType.Fleet);
        return $"It has {armies} of its {NationData.GetMaxArmies(ns.Nation)} armies and {fleets} of its "
             + $"{NationData.GetMaxFleets(ns.Nation)} fleets on the board.";
    }

    /// <summary>
    /// Regions a unit of this nation stands next to and could take a flag in: not already its own, not a
    /// home province (ManeuverEngine places no flag in one), and holding no foreign unit (a flag is only
    /// placed in a region newly occupied by that nation alone). Armies count adjacent land, fleets adjacent sea.
    /// It is what a Maneuver is worth in tax revenue, which nothing else in the question says.
    /// </summary>
    internal static int FlagsWithinReach(Game game, NationState ns)
    {
        var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var unit in game.Units.Where(u => u.Nation == ns.Nation))
        {
            // The same land/sea split the movement rules use, rather than a second copy of it here.
            foreach (string neighbour in MapConnectivity.GetNeighbors(unit.TerritoryId, unit.UnitType == UnitType.Fleet))
            {
                var definition = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == neighbour);
                if (definition == null) continue;

                if (definition.Nation != null) continue; // Home provinces never take a flag.
                if (game.TerritoryStates.Any(ts => ts.TerritoryId == neighbour && ts.Controller == ns.Nation)) continue;
                if (game.Units.Any(u => u.TerritoryId == neighbour && u.Nation != ns.Nation)) continue;

                reachable.Add(neighbour);
            }
        }

        return reachable.Count;
    }

    /// <summary>
    /// The position, as any player at the table can see it: no bot types, no strategy names, nothing
    /// about how an opponent decides (AGENTS.md - a position is evaluated the same way whoever is across
    /// the table).
    /// </summary>
    internal static object BuildState(Game game, NationState ns, Player controller)
    {
        var acting = new Dictionary<string, object?>
        {
            ["nation"] = ns.Nation.ToString(),
            ["power_points"] = ns.Power,
            ["power_factor"] = RondelData.GetPowerFactor(ns.Power),
            ["treasury_millions"] = ns.Treasury,
            ["tax_revenue_last_taxation"] = ns.TaxRevenue,
            // Not every decision happens after the marker is placed: an investor turn can come first, and
            // the first placement is not a move (p.6). Reading .Value here threw and killed the bot turn.
            ["marker_on"] = ns.RondelPosition.HasValue ? RondelData.GetSlotName(ns.RondelPosition.Value) : "not on the rondel yet",
            ["factories_unblocked"] = TaxationHelper.CountUnblockedFactories(game, ns.Nation),
            ["flags"] = $"{game.TerritoryStates.Count(ts => ts.Controller == ns.Nation)} of {TaxationRules.MaxFlagsPerNation}",
            ["armies"] = $"{game.Units.Count(u => u.Nation == ns.Nation && u.UnitType == UnitType.Army)} of {NationData.GetMaxArmies(ns.Nation)}",
            ["fleets"] = $"{game.Units.Count(u => u.Nation == ns.Nation && u.UnitType == UnitType.Fleet)} of {NationData.GetMaxFleets(ns.Nation)}",
            ["soldiers_pay_at_every_taxation_millions"] = game.Units.Count(u => u.Nation == ns.Nation),
            ["each_power_factor_point_of_this_nation_is_worth_to_its_government"] =
                game.Bonds.Where(b => b.Nation == ns.Nation && b.HolderId == ns.ControllerId).Sum(b => b.Interest),
            ["regions_next_to_its_units_it_could_take_a_flag_in"] = FlagsWithinReach(game, ns),
            ["home_provinces_occupied_by_others"] = TerritoryData.AllTerritories.Count(t => t.Nation == ns.Nation
                && game.Units.Any(u => u.TerritoryId == t.Id && u.UnitType == UnitType.Army && u.Nation != ns.Nation && u.IsHostile))
        };

        var government = new Dictionary<string, object?>
        {
            ["cash_millions"] = controller.Cash,
            ["score"] = game.CalculateScore(controller.Id),
            ["nations_this_government_controls"] = game.NationStates.Where(other => other.ControllerId == controller.Id)
                .Select(other => other.Nation.ToString()).ToList(),
            ["bonds_held"] = game.Bonds.Where(bond => bond.HolderId == controller.Id)
                .OrderBy(bond => bond.Nation).ThenByDescending(bond => bond.Interest)
                .Select(bond => $"{bond.Nation} {bond.Cost}m at {bond.Interest} interest").ToList()
        };

        var nations = game.NationStates.OrderByDescending(other => other.Power).Select(other => new Dictionary<string, object?>
        {
            ["nation"] = other.Nation.ToString(),
            ["power_points"] = other.Power,
            ["treasury_millions"] = other.Treasury,
            ["marker_on"] = other.RondelPosition.HasValue ? RondelData.GetSlotName(other.RondelPosition.Value) : "not on the rondel yet",
            ["same_government"] = other.ControllerId == controller.Id
        }).ToList();

        var rivals = game.Players.Where(player => player.Id != controller.Id)
            .Select(player => new Dictionary<string, object?>
            {
                ["score"] = game.CalculateScore(player.Id),
                ["cash_millions"] = player.Cash,
                ["bond_interest_by_nation"] = game.Bonds.Where(bond => bond.HolderId == player.Id)
                    .GroupBy(bond => bond.Nation)
                    .ToDictionary(group => group.Key.ToString(), group => group.Sum(bond => bond.Interest))
            })
            .OrderByDescending(player => (int)player["score"]!)
            .ToList();

        // How close the game is to ending, and whether this government wants it to: the game stops the
        // moment any nation reaches 25 power (p.12), scored as it stands, so the same move can be right
        // for a government that is ahead and wrong for one that is behind.
        var closest = game.NationStates.OrderByDescending(other => other.Power).First();
        var scores = game.Players.Select(player => (player.Id, Score: game.CalculateScore(player.Id)))
            .OrderByDescending(entry => entry.Score).ToList();

        return new Dictionary<string, object?>
        {
            ["game"] = "Imperial 2030",
            ["the_board"] = BuildBoard(game, ns),
            ["the_game_ends_when"] = $"any nation reaches {GameConstants.MaxPowerPoints} power points and everyone scores as they stand; "
                + $"{closest.Nation} is nearest at {closest.Power}, {GameConstants.MaxPowerPoints - closest.Power} short",
            ["this_government_is_placed"] = $"{scores.FindIndex(entry => entry.Id == controller.Id) + 1} of {scores.Count} on score",
            ["acting_nation"] = acting,
            ["this_nations_government"] = government,
            ["all_nations"] = nations,
            ["other_players"] = rivals,
            ["bonds_still_in_the_bank"] = game.Bonds.Count(bond => bond.HolderId == null),
            ["power_points_to_end_the_game"] = GameConstants.MaxPowerPoints
        };
    }

    /// <summary>The question ids; an answer comes back under the id it was asked with.</summary>
    public const string RondelQuestionId = "rondel_space";

    public const string BondQuestionId = "bond_to_buy";
    public const string FactoryCityQuestionId = "factory_city";
    public const string ImportQuestionId = "unit_to_buy";
    public const string ManeuverQuestionId = "where_this_unit_goes";
    public const string HostilityQuestionId = "stand_upright";
    public const string BattleQuestionId = "fight_or_retreat";
    public const string FactoryDestructionQuestionId = "destroy_the_factory";

    /// <summary>
    /// Nothing to ask when there is no key, when this decision is not one of the configured ones, when
    /// training (which must never wait on an outside service), or when there is no real choice.
    /// </summary>
    private bool CannotAsk(string questionId, int optionCount)
        => _typeSafe == null || !_typeSafe.IsConfigured || !_typeSafe.Asks(questionId) || IsTraining || optionCount < 2;

    // --- Investing ----------------------------------------------------------------------------------

    public async Task<Bond?> ReconsiderBondAsync(Game game, Player actor, IReadOnlyList<Bond> affordable, Bond? pick)
    {
        // Passing is always an option (p.11: the investor "may" invest), so there are always at least two.
        if (CannotAsk(BondQuestionId, affordable.Count + 1)) return null;

        const string pass = "buy nothing this time";
        var options = new Dictionary<string, Bond>(StringComparer.OrdinalIgnoreCase);
        var criteria = new Dictionary<string, object?> { [pass] = DescribePassingOnABond(game, actor) };

        foreach (var bond in affordable.OrderBy(b => b.Nation).ThenBy(b => b.Cost))
        {
            string name = $"{bond.Nation} {bond.Cost}m bond, {bond.Interest} interest";
            options[name] = bond;
            criteria[name] = DescribeBond(game, actor, bond);
        }

        var question = new ChoiceQuestion(
            new Dictionary<string, object?>
            {
                ["question"] = $"Which bond should this player buy for {actor.Cash}m in hand?",
                ["how_to_decide"] = new[]
                {
                    "A bond is paid for once and then scores its interest times the power factor of its nation at the end, every turn it is held.",
                    "The largest credit sum in a nation governs it, which hands over that nation's whole turn - its taxation, its armies and its treasury.",
                    "Money kept is score too: an unspent million is a point. A bond only beats keeping the cash if the nation it funds will gain power."
                },
                ["your_own_policy_chose"] = pick == null ? pass : options.FirstOrDefault(entry => entry.Value.Id == pick.Id).Key ?? pass
            },
            criteria);

        var answer = await AskAsync(game, BondQuestionId, question, BuildState(game, GoverningNation(game, actor), actor));
        if (answer == null || !options.TryGetValue(answer.Choice, out var chosen)) return null;

        return answer.Choice.Equals(pass, StringComparison.OrdinalIgnoreCase) ? pick : chosen;
    }

    private static string DescribePassingOnABond(Game game, Player actor)
        => $"Keep the {actor.Cash}m. It is {actor.Cash} points of score as it stands, and the bonds on offer will still be there "
         + "unless another player takes them first.";

    private string DescribeBond(Game game, Player actor, Bond bond)
    {
        var ns = game.NationStates.First(state => state.Nation == bond.Nation);
        int factor = RondelData.GetPowerFactor(ns.Power);
        int ownCredit = game.Bonds.Where(b => b.Nation == bond.Nation && b.HolderId == actor.Id).Sum(b => b.Cost);
        var governingCredit = game.Bonds.Where(b => b.Nation == bond.Nation && b.HolderId != null)
            .GroupBy(b => b.HolderId!.Value)
            .Select(group => (Holder: group.Key, Credit: group.Sum(b => b.Cost)))
            .OrderByDescending(entry => entry.Credit)
            .ToList();
        int leadingCredit = governingCredit.Count == 0 ? 0 : governingCredit[0].Credit;
        bool wouldGovern = ownCredit + bond.Cost > leadingCredit && (governingCredit.Count == 0 || governingCredit[0].Holder != actor.Id);
        bool alreadyGoverns = ns.ControllerId == actor.Id;

        return $"{bond.Nation} is at {ns.Power} power (factor {factor}), treasury {ns.Treasury}m, "
             + $"{TaxationHelper.CountUnblockedFactories(game, bond.Nation)} unblocked factories and "
             + $"{game.TerritoryStates.Count(ts => ts.Controller == bond.Nation)} flags. "
             + $"The bond costs {bond.Cost}m and scores {bond.Interest} times that factor, so {bond.Interest * factor} points at today's power "
             + $"and {bond.Interest} more for every factor point it gains. "
             + (alreadyGoverns
                 ? "This player already governs it."
                 : wouldGovern
                     ? $"Buying it would take the government of {bond.Nation} from the current leader, whose credit is {leadingCredit}m."
                     : $"The largest credit in it is {leadingCredit}m, so this would not take the government.");
    }

    // --- Building a factory -------------------------------------------------------------------------

    public async Task<string?> ReconsiderFactoryCityAsync(Game game, NationState ns, Player controller, IReadOnlyList<Territory> buildable, string pick)
    {
        if (CannotAsk(FactoryCityQuestionId, buildable.Count)) return null;

        var criteria = buildable.ToDictionary(city => city.Id, city => (object?)DescribeFactoryCity(game, ns, city));

        var question = new ChoiceQuestion(
            new Dictionary<string, object?>
            {
                ["question"] = $"Which of {ns.Nation}'s home cities should take the factory it is building for {GameConstants.FactoryCost}m?",
                ["how_to_decide"] = new[]
                {
                    "A factory produces one unit on every Production and adds 2m to tax revenue on every Taxation, for the rest of the game.",
                    "A brown city builds armies, a light blue city builds fleets - and a fleet can only ever leave from a harbour.",
                    "A hostile army standing in the province stops that factory producing and taxing, so a city exposed to one is worth less."
                },
                ["your_own_policy_chose"] = pick
            },
            criteria);

        var answer = await AskAsync(game, FactoryCityQuestionId, question, BuildState(game, ns, controller));
        return answer != null && criteria.ContainsKey(answer.Choice) ? answer.Choice : null;
    }

    private static string DescribeFactoryCity(Game game, NationState ns, Territory city)
    {
        int neighbouringEnemies = MapConnectivity.GetNeighbors(city.Id, isFleet: false)
            .Count(neighbour => game.Units.Any(u => u.TerritoryId == neighbour && u.UnitType == UnitType.Army && u.Nation != ns.Nation));
        int ownUnits = game.Units.Count(u => u.TerritoryId == city.Id && u.Nation == ns.Nation);

        return $"{city.Name} is a {(city.CityType == CityType.LightBlue ? "light blue city, so a shipyard: it would build fleets" : "brown city, so an armament facility: it would build armies")}. "
             + $"{ownUnits} of this nation's units stand there, and {neighbouringEnemies} foreign armies are one move away from it.";
    }

    // --- Importing ----------------------------------------------------------------------------------

    public async Task<(UnitType Type, string TerritoryId)?> ReconsiderImportAsync(Game game, NationState ns, Player controller,
        IReadOnlyList<(UnitType Type, string TerritoryId)> placements, (UnitType Type, string TerritoryId) pick, int remaining)
    {
        if (CannotAsk(ImportQuestionId, placements.Count)) return null;

        var options = new Dictionary<string, (UnitType Type, string TerritoryId)>(StringComparer.OrdinalIgnoreCase);
        var criteria = new Dictionary<string, object?>();
        foreach (var placement in placements)
        {
            string name = $"{placement.Type} in {placement.TerritoryId}";
            options[name] = placement;
            criteria[name] = DescribeImport(game, ns, placement);
        }

        var question = new ChoiceQuestion(
            new Dictionary<string, object?>
            {
                ["question"] = $"{ns.Nation} is buying units at {GameConstants.ImportUnitCost}m each from a treasury of {ns.Treasury}m, "
                             + $"up to {remaining} more this turn. Which one next?",
                ["how_to_decide"] = new[]
                {
                    "A new unit stays in the city it is bought into until a Maneuver moves it, and costs 1m of soldiers' pay at every Taxation from now on.",
                    "An army takes and holds land regions; a fleet takes sea regions and carries armies across them, and can only start from a harbour.",
                    "Buying into a province a foreign army is about to reach defends it; buying into a quiet corner does nothing until it has walked out."
                },
                ["your_own_policy_chose"] = $"{pick.Type} in {pick.TerritoryId}"
            },
            criteria);

        var answer = await AskAsync(game, ImportQuestionId, question, BuildState(game, ns, controller));
        return answer != null && options.TryGetValue(answer.Choice, out var chosen) ? chosen : null;
    }

    private static string DescribeImport(Game game, NationState ns, (UnitType Type, string TerritoryId) placement)
    {
        var city = TerritoryData.AllTerritories.First(t => t.Id == placement.TerritoryId);
        int ownUnits = game.Units.Count(u => u.TerritoryId == placement.TerritoryId && u.Nation == ns.Nation);
        int reachableEnemies = MapConnectivity.GetNeighbors(placement.TerritoryId, isFleet: false)
            .Count(neighbour => game.Units.Any(u => u.TerritoryId == neighbour && u.UnitType == UnitType.Army && u.Nation != ns.Nation));
        bool hasFactory = game.TerritoryStates.Any(ts => ts.TerritoryId == placement.TerritoryId && ts.HasFactory);

        return $"{placement.Type} into {city.Name}: {ownUnits} of this nation's units are there already, "
             + $"{reachableEnemies} foreign armies are one move away, and the province "
             + (hasFactory ? "holds one of this nation's factories, which a hostile army would shut down." : "has no factory.");
    }

    // --- Maneuvering --------------------------------------------------------------------------------

    public async Task<string?> ReconsiderManeuverDestinationAsync(Game game, Unit unit, Player controller, IReadOnlyList<string> destinations, string pick)
    {
        var ns = game.NationStates.FirstOrDefault(state => state.Nation == unit.Nation);
        if (ns == null || CannotAsk(ManeuverQuestionId, destinations.Count)) return null;
        if (!AskOnEveryMove && !DestinationChoiceMatters(game, unit, destinations)) return null;

        var criteria = destinations.ToDictionary(
            destination => destination,
            destination => (object?)DescribeDestination(game, unit, destination));

        int stillToMove = game.Units.Count(u => u.Nation == unit.Nation && u.UnitType == unit.UnitType && !u.HasMoved && u.Id != unit.Id);
        var question = new ChoiceQuestion(
            new Dictionary<string, object?>
            {
                ["question"] = $"Where should this {unit.UnitType.ToString().ToLowerInvariant()} of {unit.Nation}, now in "
                             + $"{TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == unit.TerritoryId)?.Name ?? unit.TerritoryId}, move?",
                ["the_rest_of_this_maneuver"] = stillToMove == 0
                    ? "It is the last of this nation's units to move in this phase."
                    : $"{stillToMove} more of this nation's {(unit.UnitType == UnitType.Army ? "armies" : "fleets")} move after it, one at a time; "
                      + "`the_board` says which of them have gone already and where they are now, so this move can be made with the rest in mind.",
                ["how_to_decide"] = new[]
                {
                    "A region a nation ends up holding alone takes its flag, and each flag is 1m more tax revenue at every Taxation - never a home province, anyone's.",
                    "An army standing upright in a foreign home province shuts down its factory: it cannot produce and it cannot be taxed while the army is there.",
                    "Staying where it is keeps a flag already held and a blockade already in place; moving off a region can hand it back.",
                    "Fleets move between sea regions and carry armies; an army that walks into a region holding a foreign unit is offering a battle, which destroys units one for one.",
                    "Two of this nation's units in one quiet region do the work of one. Spread them over regions that are worth holding, and keep one where a foreign army could walk into a factory."
                },
                ["your_own_policy_chose"] = pick
            },
            criteria);

        var answer = await AskAsync(game, ManeuverQuestionId, question, BuildState(game, ns, controller));
        return answer != null && criteria.ContainsKey(answer.Choice) ? answer.Choice : null;
    }

    /// <summary>
    /// Whether this unit has anything to decide. Most units in most maneuvers are shuffling between
    /// regions that do the same thing for them; the question is worth a call when its destinations differ
    /// in what they would actually do - take a flag, walk into a foreign home province, meet foreign
    /// units, or hold a flag the nation already has.
    ///
    /// This is about what a call is worth, not about what the rules allow: every destination the bot path
    /// offers stays on the table, and the model picks when this says there is nothing to weigh. Maneuver
    /// was 58 of the 87 calls a game before it.
    /// </summary>
    private static bool DestinationChoiceMatters(Game game, Unit unit, IReadOnlyList<string> destinations)
    {
        var kinds = destinations.Select(destination => ConsequenceOf(game, unit, destination)).ToHashSet();
        return kinds.Count >= 2 && kinds.Any(kind => kind != "nothing");
    }

    private static string ConsequenceOf(Game game, Unit unit, string destination)
    {
        var definition = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == destination);
        var state = game.TerritoryStates.FirstOrDefault(ts => ts.TerritoryId == destination);

        if (game.Units.Any(u => u.TerritoryId == destination && u.Nation != unit.Nation)) return "contested";
        if (definition?.Nation is { } owner) return owner == unit.Nation ? "own home" : "foreign home";
        if (state?.Controller == unit.Nation) return destination == unit.TerritoryId ? "holds its flag" : "nothing";
        return "takes a flag";
    }

    private static string DescribeDestination(Game game, Unit unit, string destination)
    {
        var definition = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == destination);
        var state = game.TerritoryStates.FirstOrDefault(ts => ts.TerritoryId == destination);
        var foreign = game.Units.Where(u => u.TerritoryId == destination && u.Nation != unit.Nation).ToList();
        var own = game.Units.Where(u => u.TerritoryId == destination && u.Nation == unit.Nation && u.Id != unit.Id).ToList();
        bool staying = destination == unit.TerritoryId;

        string holder = state?.Controller != null
            ? (state.Controller == unit.Nation ? "already flagged by this nation" : $"flagged by {state.Controller}")
            : "flagged by nobody";
        string home = definition?.Nation is { } owner
            ? $"a home province of {owner}, which can never take a flag"
              + (state?.HasFactory == true ? $", and it holds {(owner == unit.Nation ? "this nation's own" : "their")} factory" : "")
            : holder;
        string company = foreign.Count == 0
            ? (own.Count == 0 ? "nobody else there" : $"{own.Count} of this nation's own units already there")
            : string.Join(" and ", foreign.GroupBy(u => (u.Nation, u.UnitType)).Select(group =>
                  $"{group.Count()} {group.Key.Nation} {group.Key.UnitType.ToString().ToLowerInvariant()}{(group.Count() == 1 ? "" : "s")}"))
              + (own.Count == 0 ? "" : $", and {own.Count} of this nation's own");

        // What it would be next to from there: the threats and openings the move creates, which is the
        // part a one-line description left out entirely.
        var onward = MapConnectivity.GetNeighbors(destination, unit.UnitType == UnitType.Fleet)
            .Select(neighbour =>
            {
                var enemies = game.Units.Where(u => u.TerritoryId == neighbour && u.Nation != unit.Nation).ToList();
                string name = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == neighbour)?.Name ?? neighbour;
                return enemies.Count == 0 ? name : $"{name} ({enemies.Count} foreign unit(s))";
            })
            .ToList();

        string leaving = "";
        if (!staying)
        {
            var hereState = game.TerritoryStates.FirstOrDefault(ts => ts.TerritoryId == unit.TerritoryId);
            bool lastOneHere = !game.Units.Any(u => u.TerritoryId == unit.TerritoryId && u.Nation == unit.Nation && u.Id != unit.Id);
            if (hereState?.Controller == unit.Nation && lastOneHere)
            {
                leaving = " Leaving means no unit of this nation is left where it stands, so another nation moving in takes that flag.";
            }
        }

        // Spelled out rather than implied: a region nobody has flagged, with nobody else in it, is a
        // flag this nation takes by ending the maneuver there - which is the whole point of moving.
        string gain = definition?.Nation == null && state?.Controller != unit.Nation && foreign.Count == 0
            ? $" Ending the maneuver there alone takes its flag, worth {TaxationRules.RevenuePerFlag}m of tax revenue at every Taxation."
            : "";

        return (staying ? $"Stay in {definition?.Name ?? destination}. " : $"Move to {definition?.Name ?? destination}. ")
             + $"It is {home}, with {company}."
             + (staying && state?.Controller == unit.Nation ? " Staying keeps that flag." : "")
             + gain
             + leaving
             + (onward.Count == 0 ? "" : $" From there it would be next to: {string.Join(", ", onward)}.");
    }

    // --- The yes/no decisions -----------------------------------------------------------------------

    public async Task<bool?> ReconsiderHostilityAsync(Game game, Unit unit, Player controller, string territoryId, bool pick)
    {
        var ns = game.NationStates.FirstOrDefault(state => state.Nation == unit.Nation);
        if (ns == null || CannotAsk(HostilityQuestionId, 2)) return null;

        var definition = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == territoryId);
        var foreign = game.Units.Where(u => u.TerritoryId == territoryId && u.Nation != unit.Nation && u.Id != unit.Id).ToList();
        bool hasFactory = game.TerritoryStates.Any(ts => ts.TerritoryId == territoryId && ts.HasFactory);

        // Standing upright matters where it offers somebody a battle, or where it shuts a factory down.
        // In an empty province with nothing in it, the posture decides nothing worth a call.
        if (!AskOnEveryMove && foreign.Count == 0 && !hasFactory) return null;

        var question = new NoulQuestion(
            new Dictionary<string, object?>
            {
                ["question"] = $"Should this {unit.UnitType.ToString().ToLowerInvariant()} of {unit.Nation} stand upright in {definition?.Name ?? territoryId}?",
                ["the_region"] = $"{(definition?.Nation is { } owner ? $"a home province of {owner}" : "neutral ground")}"
                               + $"{(hasFactory ? " holding a factory" : "")}, with "
                               + (foreign.Count == 0 ? "no foreign units in it" : $"{foreign.Count} foreign unit(s) in it"),
                ["your_own_policy_chose"] = pick ? "stand upright" : "lie down"
            },
            WhenTrue: "Stand upright: it occupies the region. In a foreign home province that shuts the factory down and stops it being taxed, "
                    + "and where foreign units are present it offers them a battle, which destroys units one for one.",
            WhenFalse: "Lie down: it passes through peacefully, takes no flag away from anyone and offers no battle.");

        var answer = await AskNoulAsync(game, HostilityQuestionId, question, BuildState(game, ns, controller));
        return answer == null ? null : answer.IsYes;
    }

    public async Task<bool?> ReconsiderBattleResponseAsync(Game game, PendingBattle battle, Player controller, bool fightPick)
    {
        var ns = game.NationStates.FirstOrDefault(state => state.Nation == battle.DefenderNations.FirstOrDefault());
        if (ns == null || CannotAsk(BattleQuestionId, 2)) return null;

        var defenders = game.Units.Where(u => u.TerritoryId == battle.TerritoryId && battle.DefenderNations.Contains(u.Nation)).ToList();
        var attackers = game.Units.Where(u => u.TerritoryId == battle.TerritoryId && u.Nation == battle.AggressorNation).ToList();

        var question = new NoulQuestion(
            new Dictionary<string, object?>
            {
                ["question"] = $"{battle.AggressorNation} has moved into {battle.TerritoryId}. Should the defender fight?",
                ["the_region"] = $"{defenders.Count} defending unit(s) against {attackers.Count} attacking unit(s)",
                ["your_own_policy_chose"] = fightPick ? "fight" : "retreat"
            },
            WhenTrue: "Fight: units are destroyed one for one, so both sides lose as many as the smaller side has. It denies the attacker the region "
                    + "and the flag, at the cost of this nation's own units, each of which cost a million to buy and a million a turn to keep.",
            WhenFalse: "Retreat: the units survive and the attacker takes the region, its flag, and whatever occupying it is worth to them.");

        var answer = await AskNoulAsync(game, BattleQuestionId, question, BuildState(game, ns, controller));
        return answer == null ? null : answer.IsYes;
    }

    public async Task<bool?> ReconsiderFactoryDestructionAsync(Game game, Nation nation, string territoryId, Player controller, bool destroyPick)
    {
        var ns = game.NationStates.FirstOrDefault(state => state.Nation == nation);
        if (ns == null || CannotAsk(FactoryDestructionQuestionId, 2)) return null;

        var owner = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == territoryId)?.Nation;
        int bondsHeldInOwner = owner == null ? 0 : game.Bonds.Where(b => b.Nation == owner && b.HolderId == controller.Id).Sum(b => b.Interest);

        var question = new NoulQuestion(
            new Dictionary<string, object?>
            {
                ["question"] = $"{nation}'s armies hold the factory in {territoryId}. Should they pull it down?",
                ["the_factory"] = $"it belongs to {owner?.ToString() ?? "nobody"}, and this government holds {bondsHeldInOwner} interest worth of that nation's bonds",
                ["your_own_policy_chose"] = destroyPick ? "destroy it" : "leave it standing"
            },
            WhenTrue: "Destroy it: that nation loses a unit every Production and 2m of tax revenue every Taxation, permanently - including whatever "
                    + "this government earns from the bonds it holds in that nation, whose score follows its power.",
            WhenFalse: "Leave it standing: occupying it already stops it producing and being taxed for as long as the armies stay, and the factory is "
                    + "still there to use if this nation's bonds in it pay off later.");

        var answer = await AskNoulAsync(game, FactoryDestructionQuestionId, question, BuildState(game, ns, controller));
        return answer == null ? null : answer.IsYes;
    }

    // --- Shared plumbing ----------------------------------------------------------------------------

    /// <summary>
    /// Asks, then holds the answer to the same bar as the rondel one: an answer Jev is less sure of than
    /// <see cref="MinimumConfidence"/> is dropped, and the decision the bot had already made stands.
    /// </summary>
    private async Task<ChoiceAnswer?> AskAsync(Game game, string questionId, ChoiceQuestion question, object state)
    {
        if (_typeSafe == null) return null;

        var answer = await _typeSafe.AskChoiceAsync(state, questionId, question);
        if (answer == null) return null;

        if (answer.Confidence < MinimumConfidence)
        {
            _logger.LogInformation("Jev was {Confidence:0.00} confident about {QuestionId}, keeping the policy's decision",
                                   answer.Confidence, questionId);
            return null;
        }

        return answer;
    }

    /// <summary>As <see cref="AskAsync"/>, for a yes/no question: how far from a coin flip it landed is the bar.</summary>
    private async Task<NoulAnswer?> AskNoulAsync(Game game, string questionId, NoulQuestion question, object state)
    {
        if (_typeSafe == null) return null;

        var answer = await _typeSafe.AskNoulAsync(state, questionId, question);
        if (answer == null) return null;

        if (answer.Decisiveness < MinimumConfidence)
        {
            _logger.LogInformation("Jev answered {QuestionId} at {Probability:0.00}, too near a coin flip to overrule the policy",
                                   questionId, answer.Probability);
            return null;
        }

        return answer;
    }

    /// <summary>
    /// The nation a decision belongs to when the decision is the player's rather than a nation's - an
    /// investment is made by a player, but the state is written from a nation's point of view.
    /// </summary>
    private static NationState GoverningNation(Game game, Player actor)
        => game.NationStates.FirstOrDefault(ns => ns.ControllerId == actor.Id)
           ?? game.NationStates.OrderByDescending(ns => ns.Power).First();
}
