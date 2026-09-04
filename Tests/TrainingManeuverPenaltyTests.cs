using System;
using System.Collections.Generic;
using Imperial2030.Server.Models;
using Imperial2030.Server.Services;
using Imperial2030.Shared.Models;
using Xunit;

namespace Imperial2030.Tests;

/// <summary>
/// Two RL maneuver penalties, both added because the reward function priced the good move and left the
/// bad one free.
///
/// Observed live, in one session:
///   [00:13:43] Bot Delta (RL-4)   China  army stayed in Korea
///   [00:13:45] Bot Delta (RL-4)   China  army stayed in Korea
/// with a hostile army sitting in Beijing, which Korea borders. Relief paid +5; declining cost nothing,
/// and "Do Not Move" is unconditionally legal at every maneuver step.
///
///   [00:16:51] Bot Charlie (RL-4) USA    army moved to Alaska from Chicago (Hostile: no)
///   [00:16:53] Bot Charlie (RL-4) USA    army moved to Alaska from Chicago (Hostile: no)
///   [00:16:55] Bot Charlie (RL-4) USA    army moved to Alaska from Chicago (Hostile: no)
/// Three armies, one flag - p.10 gives the flag to the first, so the other two spent a maneuver on
/// nothing.
/// </summary>
public class TrainingManeuverPenaltyTests
{
    private static Game NewGame() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Maneuver penalties",
        Status = GameStatus.InProgress,
        CurrentTurnNation = Nation.China,
        CurrentManeuverPhase = ManeuverPhase.Armies,
        Players = new List<Player>(),
        NationStates = new List<NationState>(),
        TerritoryStates = new List<TerritoryState>(),
        Units = new List<Unit>(),
        Actions = new List<GameAction>()
    };

    private static Unit Army(Nation nation, string territoryId, bool hostile = false) => new()
    {
        Id = Guid.NewGuid(),
        Nation = nation,
        UnitType = UnitType.Army,
        TerritoryId = territoryId,
        IsHostile = hostile
    };

    // ---- declined home relief -------------------------------------------------------------------

    /// <summary>Korea borders Beijing (MapConnectivity), so the relief really is one move away.</summary>
    [Fact]
    public void AnArmyThatCouldRelieveItsBlockadedHomeIsOfferedThatMove()
    {
        var game = NewGame();
        var korea = Army(Nation.China, "Korea");
        game.Units.Add(korea);
        game.Units.Add(Army(Nation.Brazil, "Beijing", hostile: true));

        Assert.Equal("Beijing", TcpTrainingServer.ReachableBlockadedHomeProvince(game, korea));
    }

    /// <summary>A null destination is the "Do Not Move" action, which is what the bot actually chose.</summary>
    [Fact]
    public void StayingPutInsteadOfRelievingIsPenalized()
    {
        var game = NewGame();
        var korea = Army(Nation.China, "Korea");
        game.Units.Add(korea);
        game.Units.Add(Army(Nation.Brazil, "Beijing", hostile: true));

        Assert.Equal(TcpTrainingServer.DeclinedHomeReliefPenalty,
            TcpTrainingServer.DeclinedHomeReliefPenaltyFor(game, korea, null));
    }

    [Fact]
    public void MovingSomewhereElseInsteadOfRelievingIsPenalized()
    {
        var game = NewGame();
        var korea = Army(Nation.China, "Korea");
        game.Units.Add(korea);
        game.Units.Add(Army(Nation.Brazil, "Beijing", hostile: true));

        Assert.Equal(TcpTrainingServer.DeclinedHomeReliefPenalty,
            TcpTrainingServer.DeclinedHomeReliefPenaltyFor(game, korea, "Vladivostok"));
    }

    [Fact]
    public void ActuallyRelievingCostsNothing()
    {
        var game = NewGame();
        var korea = Army(Nation.China, "Korea");
        game.Units.Add(korea);
        game.Units.Add(Army(Nation.Brazil, "Beijing", hostile: true));

        Assert.Equal(0f, TcpTrainingServer.DeclinedHomeReliefPenaltyFor(game, korea, "Beijing"));
    }

    /// <summary>
    /// A friendly foreign army is not a blockade. p.10 blockades specifically with a HOSTILE army
    /// standing upright; a laid-down foreign army imposes no constraint at all.
    /// </summary>
    [Fact]
    public void AFriendlyForeignArmyAtHomeIsNotABlockade()
    {
        var game = NewGame();
        var korea = Army(Nation.China, "Korea");
        game.Units.Add(korea);
        game.Units.Add(Army(Nation.Brazil, "Beijing", hostile: false));

        Assert.Null(TcpTrainingServer.ReachableBlockadedHomeProvince(game, korea));
        Assert.Equal(0f, TcpTrainingServer.DeclinedHomeReliefPenaltyFor(game, korea, null));
    }

    /// <summary>Out of reach is not a declined option - the agent was never offered it.</summary>
    [Fact]
    public void AnUnreachableBlockadeIsNotPenalized()
    {
        var game = NewGame();
        var far = Army(Nation.China, "Brasilia");
        game.Units.Add(far);
        game.Units.Add(Army(Nation.Brazil, "Beijing", hostile: true));

        Assert.Null(TcpTrainingServer.ReachableBlockadedHomeProvince(game, far));
    }

    /// <summary>
    /// p.7 blockades are caused by armies. A fleet cannot enter a land province to contest one, so it
    /// must never be charged for failing to.
    /// </summary>
    [Fact]
    public void AFleetIsNeverChargedForALandBlockade()
    {
        var game = NewGame();
        var fleet = new Unit { Id = Guid.NewGuid(), Nation = Nation.China, UnitType = UnitType.Fleet, TerritoryId = "ChinaSea" };
        game.Units.Add(fleet);
        game.Units.Add(Army(Nation.Brazil, "Beijing", hostile: true));

        Assert.Null(TcpTrainingServer.ReachableBlockadedHomeProvince(game, fleet));
    }

    // ---- redundant stacking ---------------------------------------------------------------------

    [Fact]
    public void PilingASecondArmyOntoAnUncontestedNeutralRegionIsRedundant()
    {
        var game = NewGame();
        var mover = Army(Nation.USA, "Chicago");
        game.Units.Add(mover);
        game.Units.Add(Army(Nation.USA, "Alaska"));

        Assert.True(TcpTrainingServer.IsRedundantStackMove(game, mover, "Alaska"));
    }

    /// <summary>The first army takes the flag, so it is never the redundant one.</summary>
    [Fact]
    public void TheFirstArmyIntoAnEmptyNeutralRegionIsNotRedundant()
    {
        var game = NewGame();
        var mover = Army(Nation.USA, "Chicago");
        game.Units.Add(mover);

        Assert.False(TcpTrainingServer.IsRedundantStackMove(game, mover, "Alaska"));
    }

    /// <summary>Anything foreign there makes it reinforcement for a fight, not waste.</summary>
    [Fact]
    public void StackingOntoAContestedRegionIsNotRedundant()
    {
        var game = NewGame();
        var mover = Army(Nation.USA, "Chicago");
        game.Units.Add(mover);
        game.Units.Add(Army(Nation.USA, "Alaska"));
        game.Units.Add(Army(Nation.Russia, "Alaska"));

        Assert.False(TcpTrainingServer.IsRedundantStackMove(game, mover, "Alaska"));
    }

    /// <summary>
    /// Three armies destroy a foreign factory (p.11) and armies blockade (p.10) - both need numbers, so
    /// massing on a foreign home province must stay free.
    /// </summary>
    [Fact]
    public void StackingOnAForeignHomeProvinceIsNotRedundant()
    {
        var game = NewGame();
        var mover = Army(Nation.USA, "Korea");
        game.Units.Add(mover);
        game.Units.Add(Army(Nation.USA, "Beijing"));

        Assert.False(TcpTrainingServer.IsRedundantStackMove(game, mover, "Beijing"));
    }

    /// <summary>Massing to defend a nation's own factory city is sound play.</summary>
    [Fact]
    public void StackingOnItsOwnHomeProvinceIsNotRedundant()
    {
        var game = NewGame();
        var mover = Army(Nation.USA, "Chicago");
        game.Units.Add(mover);
        game.Units.Add(Army(Nation.USA, "NewYork"));

        Assert.False(TcpTrainingServer.IsRedundantStackMove(game, mover, "NewYork"));
    }

    // ---- stacking against a reachable threat is defence, not waste ------------------------------

    /// <summary>
    /// Battles destroy 1:1 (p.10), so an attacker arriving with two armies removes two defenders. A lone
    /// army holding a region two enemies can reach is a gift; the second army is real defence.
    /// </summary>
    [Fact]
    public void ASecondArmyIsNotRedundantWhenTwoEnemiesCanReachTheRegion()
    {
        var game = NewGame();
        var mover = Army(Nation.USA, "Chicago");
        game.Units.Add(mover);
        game.Units.Add(Army(Nation.USA, "Alaska"));

        // Alaska borders only NorthPacific and Canada, so Canada is where a land threat can sit.
        game.Units.Add(Army(Nation.Russia, "Canada"));
        game.Units.Add(Army(Nation.Russia, "Canada"));

        Assert.False(TcpTrainingServer.IsRedundantStackMove(game, mover, "Alaska"));
    }

    /// <summary>
    /// Matching the threat is enough: an equal trade empties the region, and p.10 keeps the flag with its
    /// owner until someone occupies EXCLUSIVELY. So a further army beyond that adds nothing.
    /// </summary>
    [Fact]
    public void AFurtherArmyIsRedundantOnceDefendersMatchTheThreat()
    {
        var game = NewGame();
        var mover = Army(Nation.USA, "Chicago");
        game.Units.Add(mover);
        game.Units.Add(Army(Nation.USA, "Alaska"));
        game.Units.Add(Army(Nation.USA, "Alaska"));
        game.Units.Add(Army(Nation.Russia, "Canada"));

        Assert.True(TcpTrainingServer.IsRedundantStackMove(game, mover, "Alaska"));
    }

    /// <summary>
    /// A fleet that could carry an army is not itself an army and cannot take a land region, so it must
    /// not inflate the threat count and excuse a wasted stack.
    /// </summary>
    [Fact]
    public void AnEnemyFleetDoesNotCountAsAThreatToALandRegion()
    {
        var game = NewGame();
        var mover = Army(Nation.USA, "Chicago");
        game.Units.Add(mover);
        game.Units.Add(Army(Nation.USA, "Alaska"));
        game.Units.Add(new Unit { Id = Guid.NewGuid(), Nation = Nation.Russia, UnitType = UnitType.Fleet, TerritoryId = "NorthPacific" });

        Assert.True(TcpTrainingServer.IsRedundantStackMove(game, mover, "Alaska"));
    }

    /// <summary>
    /// One player can control several nations, and its own other nation is not a threat to itself.
    /// Without the friendly-nation set the second controlled nation would read as an enemy and excuse
    /// every stack that player makes.
    /// </summary>
    [Fact]
    public void AnotherNationTheSamePlayerControlsIsNotAThreat()
    {
        var game = NewGame();
        var mover = Army(Nation.USA, "Chicago");
        game.Units.Add(mover);
        game.Units.Add(Army(Nation.USA, "Alaska"));
        // Two, so that treating Russia as an enemy leaves the lone Alaska defender short of the threat
        // and the move genuinely non-redundant. One would now be matched, which is sufficient.
        game.Units.Add(Army(Nation.Russia, "Canada"));
        game.Units.Add(Army(Nation.Russia, "Canada"));

        var bothControlledBySamePlayer = new HashSet<Nation> { Nation.USA, Nation.Russia };

        Assert.True(TcpTrainingServer.IsRedundantStackMove(game, mover, "Alaska", bothControlledBySamePlayer));
        Assert.False(TcpTrainingServer.IsRedundantStackMove(game, mover, "Alaska"));
    }

    /// <summary>
    /// A blockade costs production, import, factory building, taxation and rail (p.10). Relieving one
    /// must dominate the small positional terms it is chosen against.
    /// </summary>
    [Fact]
    public void RelievingAHomeProvinceOutweighsTheSmallPositionalTerms()
    {
        Assert.True(TcpTrainingServer.HomeReliefReward > TcpTrainingServer.RedundantStackPenalty);
        Assert.True(TcpTrainingServer.DeclinedHomeReliefPenalty > TcpTrainingServer.RedundantStackPenalty);
    }
}
