using System;
using System.Collections.Generic;
using System.Linq;
using Imperial2030.Server.Models;
using Imperial2030.Server.Services;
using Imperial2030.Shared.Models;
using Xunit;

namespace Imperial2030.Tests;

/// <summary>
/// Guards the RL training penalty for stripping a factory city's last army defender while an enemy army
/// is in range of it.
///
/// The penalty exists because the reward for the move itself (a flag grab, clearing a hostile) is
/// immediate and certain, while losing the factory afterwards is delayed and opponent-dependent — so
/// without it the agent happily empties its own home cities.
///
/// Most of these cases are about when it must NOT fire. Leaving to KILL the threat is the correct answer
/// to it, not a reckless move, and penalizing that teaches the agent to sit still and let an enemy walk
/// in. The hostility flag matters for one exemption and not the other, which is the subtlety worth
/// pinning: see IsRecklessFactoryCityVacation.
/// </summary>
public class TrainingFactoryGarrisonPenaltyTests
{
    // Real adjacency (Shared/Constants/MapConnectivity.cs):
    //   Berlin  <-> Paris, Rome, Ukraine, NorthAtlantic, Murmansk
    //   Paris   <-> Berlin, Rome, MediterraneanSea, NorthAtlantic
    //   Rome    <-> Paris, Berlin, MediterraneanSea, Turkey, Ukraine
    // Berlin/Paris/Rome are Europe home provinces; Ukraine and Turkey are neutral.
    private const string Berlin = "Berlin";
    private const string Paris = "Paris";
    private const string Rome = "Rome";
    private const string Ukraine = "Ukraine";
    private const string Turkey = "Turkey";

    private static Unit Army(Nation nation, string territory, bool hostile = false) =>
        new Unit { Id = Guid.NewGuid(), Nation = nation, UnitType = UnitType.Army, TerritoryId = territory, IsHostile = hostile };

    private static Unit Fleet(Nation nation, string territory) =>
        new Unit { Id = Guid.NewGuid(), Nation = nation, UnitType = UnitType.Fleet, TerritoryId = territory };

    /// <param name="factoryCity">Europe home city holding the factory and the lone defender.</param>
    private static (Game Game, Unit Mover) GameWith(string factoryCity, params Unit[] others)
    {
        var game = new Game { Id = Guid.NewGuid(), CurrentTurnNation = Nation.Europe };
        var mover = Army(Nation.Europe, factoryCity);

        game.Units = new List<Unit> { mover };
        foreach (var u in others) game.Units.Add(u);

        game.TerritoryStates = new List<TerritoryState>
        {
            new TerritoryState { TerritoryId = factoryCity, HasFactory = true }
        };
        game.NationStates = new List<NationState>
        {
            new NationState { Nation = Nation.Europe, ControllerId = Guid.NewGuid() }
        };
        return (game, mover);
    }

    private static bool IsReckless(Game game, Unit mover, string from, string to, bool hostile) =>
        TcpTrainingServer.IsRecklessFactoryCityVacation(game, mover, from, to, hostile);

    // --- The penalty must still fire ------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LeavingAFactoryCityUndefendedWithAnEnemyInRangeIsPenalized(bool hostile)
    {
        // Berlin's only defender walks to Paris while a Russian army sits in adjacent Ukraine.
        var (game, mover) = GameWith(Berlin, Army(Nation.Russia, Ukraine));

        Assert.True(IsReckless(game, mover, Berlin, Paris, hostile));
    }

    /// <summary>
    /// Moving to one of its own home provinces is only excused when there is actually an enemy there to
    /// fight — an empty home province is just as much of an abandonment as anywhere else.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MovingToAnEmptyOwnHomeProvinceIsStillPenalized(bool hostile)
    {
        var (game, mover) = GameWith(Berlin, Army(Nation.Russia, Ukraine));

        Assert.True(IsReckless(game, mover, Berlin, Rome, hostile));
    }

    /// <summary>
    /// An enemy that cannot reach the vacated city is not a threat, so going after it answers nothing —
    /// the city is still open to the enemy that CAN reach it. Distinguishes "moved onto an enemy" from
    /// "moved onto the enemy that mattered".
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MovingOntoAnEnemyThatWasNeverAThreatIsStillPenalized(bool hostile)
    {
        var (game, mover) = GameWith(
            Berlin,
            Army(Nation.Russia, Ukraine),  // the real threat to Berlin, ignored
            Army(Nation.China, Turkey));   // unrelated enemy, out of range of Berlin

        Assert.True(IsReckless(game, mover, Berlin, Turkey, hostile));
    }

    /// <summary>
    /// The key asymmetry. Outside its own home provinces a FRIENDLY arrival does not force combat:
    /// Imperial-2030-Rules.pdf p.10 says the active nation "may destroy armies" and foreign armies "can
    /// call for a battle" — both optional — so the defender may simply decline and the two units sit in
    /// the region together, leaving the threat free to take the vacated city next turn. Answering nothing
    /// earns no exemption.
    /// </summary>
    [Fact]
    public void AFriendlyMoveOntoTheThreatIsStillPenalized()
    {
        var (game, mover) = GameWith(Berlin, Army(Nation.Russia, Ukraine));

        Assert.True(IsReckless(game, mover, Berlin, Ukraine, hostile: false));
    }

    // --- The exemptions the penalty must respect ------------------------------------------------

    /// <summary>Preventative strike: attacking the very army that could have taken the city.</summary>
    [Fact]
    public void AttackingTheEnemyThatCouldReachTheCityIsNotPenalized()
    {
        var (game, mover) = GameWith(Berlin, Army(Nation.Russia, Ukraine));

        Assert.False(IsReckless(game, mover, Berlin, Ukraine, hostile: true));
    }

    /// <summary>
    /// Defending its own home territory. Deliberately built so the enemy at the destination is a FLEET,
    /// which is not among the army threats to Paris — proving this exemption stands on its own rather
    /// than quietly riding on the preventative-strike one, which the map's dense adjacency would
    /// otherwise mask.
    ///
    /// Asserted for BOTH hostility values because the engine overrides the flag here ("Foreign armies in
    /// your home territory are always hostile. You cannot peacefully coexist."), so a battle happens
    /// either way. That keeps this correct once the hostility decision is handed to the model — today
    /// RLBotStrategy always returns true when an enemy is present, so a flag-gated version of this
    /// exemption would look fine and then quietly start penalizing correct home defence.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AttackingAnEnemyOnItsOwnHomeTerritoryIsNotPenalized(bool hostile)
    {
        var (game, mover) = GameWith(
            Paris,
            Army(Nation.Russia, Berlin),   // the actual threat to Paris
            Fleet(Nation.Russia, Rome));   // enemy sitting in Europe's own home city

        Assert.False(IsReckless(game, mover, Paris, Rome, hostile));
    }

    /// <summary>Retaking a home city an enemy holds — both exemptions apply, flag irrelevant.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RetakingAHomeCityHeldByAnEnemyIsNotPenalized(bool hostile)
    {
        var (game, mover) = GameWith(Berlin, Army(Nation.Russia, Rome, hostile: true));

        Assert.False(IsReckless(game, mover, Berlin, Rome, hostile));
    }

    // --- Pre-existing conditions that already suppressed it -------------------------------------

    [Fact]
    public void NoEnemyInRangeIsNotPenalized()
    {
        var (game, mover) = GameWith(Berlin); // nobody else on the board

        Assert.False(IsReckless(game, mover, Berlin, Paris, hostile: false));
    }

    [Fact]
    public void ARemainingDefenderMeansNoPenalty()
    {
        var (game, mover) = GameWith(Berlin, Army(Nation.Europe, Berlin), Army(Nation.Russia, Ukraine));

        Assert.False(IsReckless(game, mover, Berlin, Paris, hostile: false));
    }

    [Fact]
    public void ACityWithoutAFactoryIsNotPenalized()
    {
        var (game, mover) = GameWith(Berlin, Army(Nation.Russia, Ukraine));
        game.TerritoryStates.Single(t => t.TerritoryId == Berlin).HasFactory = false;

        Assert.False(IsReckless(game, mover, Berlin, Paris, hostile: false));
    }

    [Fact]
    public void FleetsAreNotSubjectToThisPenalty()
    {
        var (game, _) = GameWith(Berlin, Army(Nation.Russia, Ukraine));
        var fleet = Fleet(Nation.Europe, Berlin);
        game.Units.Add(fleet);

        Assert.False(IsReckless(game, fleet, Berlin, Paris, hostile: false));
    }

    [Fact]
    public void StayingPutIsNotPenalized()
    {
        var (game, mover) = GameWith(Berlin, Army(Nation.Russia, Ukraine));

        Assert.False(IsReckless(game, mover, Berlin, Berlin, hostile: false));
    }
}
