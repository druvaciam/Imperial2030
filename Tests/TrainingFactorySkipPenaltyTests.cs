using System;
using System.Collections.Generic;
using System.Linq;
using Imperial2030.Server.Models;
using Imperial2030.Server.Services;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using Xunit;

namespace Imperial2030.Tests;

/// <summary>
/// Guards the RL training penalty for landing on the Factory rondel slot, being able to build, and
/// choosing not to.
///
/// Observed live, with Europe: "moved to Factory from Maneuver (Cost: 0M)" immediately followed by
/// "ended their turn", with a treasury well over the 5M cost. Nothing in the reward function objected.
/// The existing "wasted Factory action" penalty only fires when the nation COULDN'T build (no money, or
/// every home city already built or blockaded), and RLBotStrategy.ChooseCityForFactory keeps the skip
/// action unmasked at all times — so an agent that arrived able to build and declined got no signal at
/// all, while a successful build earns +10.
///
/// The interesting half of these tests is the cases that must NOT be penalized. Skipping is a legal and
/// sometimes necessary outcome, and penalizing it indiscriminately would push the agent back into
/// avoiding the Factory slot outright — the exact failure the existing penalty's magnitude comment
/// records having already caused once.
/// </summary>
public class TrainingFactorySkipPenaltyTests
{
    // Europe's four home provinces, all of which are cities.
    private static List<Territory> EuropeHome() =>
        TerritoryData.AllTerritories.Where(t => t.Nation == Nation.Europe).OrderBy(t => t.Id).ToList();

    private static (Game Game, NationState Ns) Setup(
        int treasury,
        IEnumerable<string>? citiesWithFactory = null,
        IEnumerable<string>? citiesWithHostileArmy = null)
    {
        var game = new Game { Id = Guid.NewGuid(), CurrentTurnNation = Nation.Europe };
        var ns = new NationState
        {
            Nation = Nation.Europe,
            Treasury = treasury,
            RondelPosition = RondelData.FactorySlot,
            ControllerId = Guid.NewGuid()
        };
        game.NationStates = new List<NationState> { ns };

        game.TerritoryStates = EuropeHome()
            .Select(t => new TerritoryState
            {
                TerritoryId = t.Id,
                HasFactory = citiesWithFactory?.Contains(t.Id) ?? false
            })
            .ToList();

        game.Units = (citiesWithHostileArmy ?? Enumerable.Empty<string>())
            .Select(id => new Unit
            {
                Id = Guid.NewGuid(),
                Nation = Nation.Russia,
                UnitType = UnitType.Army,
                TerritoryId = id,
                IsHostile = true
            })
            .ToList();

        return (game, ns);
    }

    [Fact]
    public void SkippingWithAFullTreasuryAndAnEmptyCityIsPenalized()
    {
        var (game, ns) = Setup(treasury: 20);

        Assert.True(TcpTrainingServer.WasAvoidableFactorySkip(game, ns));
    }

    /// <summary>Exactly the observed case: plenty of money, one city still free.</summary>
    [Fact]
    public void SkippingWithOnlyOneRemainingBuildableCityIsPenalized()
    {
        var home = EuropeHome();
        var allButOne = home.Take(home.Count - 1).Select(t => t.Id).ToList();
        var (game, ns) = Setup(treasury: 8, citiesWithFactory: allButOne);

        Assert.True(TcpTrainingServer.WasAvoidableFactorySkip(game, ns));
    }

    [Fact]
    public void ExactlyEnoughTreasuryStillCountsAsAvoidable()
    {
        var (game, ns) = Setup(treasury: 5);

        Assert.True(TcpTrainingServer.WasAvoidableFactorySkip(game, ns));
    }

    // --- Cases that must NOT be penalized -------------------------------------------------------

    /// <summary>
    /// Already covered, more precisely, by the existing "wasted Factory action" penalty on the rondel
    /// move itself. Firing here too would double-penalize one event - see .agents/AGENTS.md rule #25.
    /// </summary>
    [Fact]
    public void SkippingWithTooLittleTreasuryIsNotPenalizedHere()
    {
        var (game, ns) = Setup(treasury: 4);

        Assert.False(TcpTrainingServer.WasAvoidableFactorySkip(game, ns));
    }

    [Fact]
    public void SkippingWithEveryCityAlreadyBuiltIsNotPenalized()
    {
        var (game, ns) = Setup(treasury: 20, citiesWithFactory: EuropeHome().Select(t => t.Id));

        Assert.False(TcpTrainingServer.WasAvoidableFactorySkip(game, ns));
    }

    /// <summary>
    /// Per Imperial-2030-Rules.pdf p.7 a factory may not be built in a home province occupied by hostile
    /// armies, so this skip is forced, not chosen.
    /// </summary>
    [Fact]
    public void SkippingWhenEveryFreeCityIsBlockadedIsNotPenalized()
    {
        var home = EuropeHome();
        var built = home.Take(home.Count - 1).Select(t => t.Id).ToList();
        var blockaded = new[] { home.Last().Id };
        var (game, ns) = Setup(treasury: 20, citiesWithFactory: built, citiesWithHostileArmy: blockaded);

        Assert.False(TcpTrainingServer.WasAvoidableFactorySkip(game, ns));
    }

    /// <summary>
    /// A FRIENDLY foreign army (laid on its side) does not block building, so a free city under one is
    /// still buildable and skipping it is still avoidable.
    /// </summary>
    [Fact]
    public void AFriendlyForeignArmyDoesNotExcuseTheSkip()
    {
        var home = EuropeHome();
        var built = home.Take(home.Count - 1).Select(t => t.Id).ToList();
        var (game, ns) = Setup(treasury: 20, citiesWithFactory: built);
        game.Units = new List<Unit>
        {
            new Unit
            {
                Id = Guid.NewGuid(),
                Nation = Nation.Russia,
                UnitType = UnitType.Army,
                TerritoryId = home.Last().Id,
                IsHostile = false
            }
        };

        Assert.True(TcpTrainingServer.WasAvoidableFactorySkip(game, ns));
    }

    /// <summary>The nation's OWN army in its city obviously does not blockade it.</summary>
    [Fact]
    public void OwnArmyInTheCityDoesNotExcuseTheSkip()
    {
        var home = EuropeHome();
        var built = home.Take(home.Count - 1).Select(t => t.Id).ToList();
        var (game, ns) = Setup(treasury: 20, citiesWithFactory: built);
        game.Units = new List<Unit>
        {
            new Unit
            {
                Id = Guid.NewGuid(),
                Nation = Nation.Europe,
                UnitType = UnitType.Army,
                TerritoryId = home.Last().Id,
                IsHostile = true
            }
        };

        Assert.True(TcpTrainingServer.WasAvoidableFactorySkip(game, ns));
    }

    /// <summary>
    /// The penalty must be decisively larger than the +10 a successful build earns, or an agent that
    /// dislikes spending treasury can still rationally skip. It is safe for it to be harsh: unlike the
    /// existing wasted-move penalty it cannot push the agent away from the Factory SLOT, because it only
    /// ever fires once the nation is already standing on it with a build genuinely available.
    /// </summary>
    [Fact]
    public void ThePenaltyDominatesTheBuildReward()
    {
        Assert.True(TcpTrainingServer.AvoidableFactorySkipPenalty > 10.0f * 2,
            "Skipping an available build must cost decisively more than building earns.");
    }

    // --- Saving for Import ----------------------------------------------------------------------
    //
    // A factory costs 5M and a full Import is 3 units at 1M each, so a nation needs 8M to afford both.
    // Between 5M and 7M the two genuinely compete: building leaves too little to import a full wave, so
    // holding the money can be a real plan rather than the agent simply wasting its turn. The skip is
    // still discouraged - a factory keeps producing every Production turn while an import buys units
    // once - but not as harshly as skipping with money to spare.

    /// <summary>8M covers a factory AND a full import, so there is nothing to save for.</summary>
    [Theory]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(30)]
    public void SkippingWithEnoughForBothFactoryAndImportCostsFullPenalty(int treasury)
    {
        var (game, ns) = Setup(treasury);

        Assert.Equal(TcpTrainingServer.AvoidableFactorySkipPenalty,
            TcpTrainingServer.AvoidableFactorySkipPenaltyFor(game, ns));
    }

    /// <summary>Can afford the factory, but building it would leave less than a full import.</summary>
    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void SkippingWhenBuildingWouldStarveAnImportCostsTheReducedPenalty(int treasury)
    {
        var (game, ns) = Setup(treasury);

        Assert.Equal(TcpTrainingServer.ReducedFactorySkipPenalty,
            TcpTrainingServer.AvoidableFactorySkipPenaltyFor(game, ns));
    }

    [Fact]
    public void TheReducedPenaltyIsSmallerThanTheFullOneButStillDiscouragesSkipping()
    {
        Assert.True(TcpTrainingServer.ReducedFactorySkipPenalty < TcpTrainingServer.AvoidableFactorySkipPenalty,
            "The import-saving case must cost less than skipping with money to spare.");
        Assert.True(TcpTrainingServer.ReducedFactorySkipPenalty > 0f,
            "Saving for an import excuses the skip partially, not entirely - a factory outlasts one import wave.");
    }

    /// <summary>Skips that were never avoidable stay at zero regardless of treasury.</summary>
    [Fact]
    public void UnavoidableSkipsCostNothingAtEitherTier()
    {
        var (poor, poorNs) = Setup(treasury: 4);
        Assert.Equal(0f, TcpTrainingServer.AvoidableFactorySkipPenaltyFor(poor, poorNs));

        var (full, fullNs) = Setup(treasury: 30, citiesWithFactory: EuropeHome().Select(t => t.Id));
        Assert.Equal(0f, TcpTrainingServer.AvoidableFactorySkipPenaltyFor(full, fullNs));
    }

    /// <summary>
    /// The 8M boundary must come from the real rules, not a hand-copied literal - the factory price and
    /// the import cap both live elsewhere and either could change.
    /// </summary>
    [Fact]
    public void TheThresholdIsFactoryCostPlusAFullImport()
    {
        const int factoryCost = 5;
        int fullImport = Imperial2030.Server.Services.Bots.Strategies.RLBotStrategy.MaxImportUnits; // 1M per unit

        var (justBelow, belowNs) = Setup(treasury: factoryCost + fullImport - 1);
        var (atThreshold, atNs) = Setup(treasury: factoryCost + fullImport);

        Assert.Equal(TcpTrainingServer.ReducedFactorySkipPenalty,
            TcpTrainingServer.AvoidableFactorySkipPenaltyFor(justBelow, belowNs));
        Assert.Equal(TcpTrainingServer.AvoidableFactorySkipPenalty,
            TcpTrainingServer.AvoidableFactorySkipPenaltyFor(atThreshold, atNs));
    }
}
