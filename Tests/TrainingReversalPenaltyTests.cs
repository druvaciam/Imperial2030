using System;
using System.Collections.Generic;
using Imperial2030.Server.Models;
using Imperial2030.Server.Services;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using Xunit;

namespace Imperial2030.Tests;

/// <summary>
/// Guards the RL training penalty for shuffling a unit back and forth between two territories.
///
/// Observed live, with China: "army moved to Chongqing from Kazakhstan" and then, next maneuver,
/// "army moved to Kazakhstan from Chongqing" - two whole turns spent to end up exactly where it
/// started. Nothing else in the reward function objects: moving costs no money, so the wasted-Rondel
/// penalties never fire, and the flag and hostile-clearing rewards simply pay nothing, leaving an
/// action with no signal at all attached to it.
///
/// The interesting half of these tests is the cases that must NOT be penalized - a penalty this
/// blunt would otherwise teach the agent to avoid perfectly good moves.
/// </summary>
public class TrainingReversalPenaltyTests
{
    private const string ChinaHome = "Chongqing";     // a China home province
    private const string Neutral = "Kazakhstan";      // neutral, flaggable

    private static Unit ChinaArmy(Guid id) =>
        new Unit { Id = id, Nation = Nation.China, UnitType = UnitType.Army, TerritoryId = Neutral };

    /// <param name="controllerOfNeutral">Who holds the neutral territory, or null for unclaimed.</param>
    private static Game GameWith(Nation? controllerOfNeutral)
    {
        var game = new Game { Id = Guid.NewGuid() };
        game.TerritoryStates = new List<TerritoryState>
        {
            new TerritoryState { TerritoryId = Neutral, Controller = controllerOfNeutral }
        };
        return game;
    }

    [Fact]
    public void ReturningToAHomeProvinceItJustLeftIsPenalized()
    {
        // Chongqing -> Kazakhstan last maneuver, now Kazakhstan -> Chongqing. A home province is never
        // flagged, so coming back to it cannot win anything.
        Assert.Equal(Nation.China, TerritoryData.AllTerritories.Find(t => t.Id == ChinaHome)!.Nation);

        var unit = ChinaArmy(Guid.NewGuid());
        var history = new Dictionary<Guid, string> { [unit.Id] = ChinaHome };

        Assert.True(TcpTrainingServer.IsPointlessReversal(
            GameWith(null), unit, origin: Neutral, target: ChinaHome, isHostileMove: false, history));
    }

    [Fact]
    public void ReturningToATerritoryThisNationAlreadyControlsIsPenalized()
    {
        var unit = ChinaArmy(Guid.NewGuid());
        unit.TerritoryId = ChinaHome;
        var history = new Dictionary<Guid, string> { [unit.Id] = Neutral };

        Assert.True(TcpTrainingServer.IsPointlessReversal(
            GameWith(Nation.China), unit, origin: ChinaHome, target: Neutral, isHostileMove: false, history));
    }

    [Fact]
    public void ReturningToATerritoryStillWorthAFlagIsNotPenalized()
    {
        // Same round trip, but the destination is unclaimed - going back wins a flag, which is a real
        // gain. Penalizing this would teach the agent to leave flags on the table.
        var unit = ChinaArmy(Guid.NewGuid());
        unit.TerritoryId = ChinaHome;
        var history = new Dictionary<Guid, string> { [unit.Id] = Neutral };

        Assert.False(TcpTrainingServer.IsPointlessReversal(
            GameWith(null), unit, origin: ChinaHome, target: Neutral, isHostileMove: false, history));
    }

    [Fact]
    public void ReturningHeldByARivalIsNotPenalized()
    {
        var unit = ChinaArmy(Guid.NewGuid());
        unit.TerritoryId = ChinaHome;
        var history = new Dictionary<Guid, string> { [unit.Id] = Neutral };

        Assert.False(TcpTrainingServer.IsPointlessReversal(
            GameWith(Nation.India), unit, origin: ChinaHome, target: Neutral, isHostileMove: false, history));
    }

    [Fact]
    public void AHostileReturnIsNotPenalized()
    {
        // An attack is an attack whichever direction it travels.
        var unit = ChinaArmy(Guid.NewGuid());
        var history = new Dictionary<Guid, string> { [unit.Id] = ChinaHome };

        Assert.False(TcpTrainingServer.IsPointlessReversal(
            GameWith(null), unit, origin: Neutral, target: ChinaHome, isHostileMove: true, history));
    }

    [Fact]
    public void StayingPutIsNotAReversal()
    {
        // origin == target. Without an explicit guard this reads as "returning to where you came from"
        // and would penalize a unit for holding its ground twice in a row.
        var unit = ChinaArmy(Guid.NewGuid());
        unit.TerritoryId = ChinaHome;
        var history = new Dictionary<Guid, string> { [unit.Id] = ChinaHome };

        Assert.False(TcpTrainingServer.IsPointlessReversal(
            GameWith(null), unit, origin: ChinaHome, target: ChinaHome, isHostileMove: false, history));
    }

    [Fact]
    public void MovingOnwardsToAThirdTerritoryIsNotPenalized()
    {
        // A -> B -> C is progress, not oscillation.
        var unit = ChinaArmy(Guid.NewGuid());
        var history = new Dictionary<Guid, string> { [unit.Id] = ChinaHome };

        Assert.False(TcpTrainingServer.IsPointlessReversal(
            GameWith(null), unit, origin: Neutral, target: "Urumqi", isHostileMove: false, history));
    }

    [Fact]
    public void AUnitWithNoRecordedHistoryIsNotPenalized()
    {
        // First move of the game: nothing to have reversed.
        var unit = ChinaArmy(Guid.NewGuid());

        Assert.False(TcpTrainingServer.IsPointlessReversal(
            GameWith(null), unit, origin: Neutral, target: ChinaHome, isHostileMove: false,
            new Dictionary<Guid, string>()));
    }

    [Fact]
    public void OneUnitsHistoryDoesNotPenalizeAnother()
    {
        // Two armies of the same nation on the same territory: the one that made the outbound trip is
        // the only one whose return is pointless.
        var traveller = ChinaArmy(Guid.NewGuid());
        var freshUnit = ChinaArmy(Guid.NewGuid());
        var history = new Dictionary<Guid, string> { [traveller.Id] = ChinaHome };

        Assert.True(TcpTrainingServer.IsPointlessReversal(
            GameWith(null), traveller, Neutral, ChinaHome, false, history));
        Assert.False(TcpTrainingServer.IsPointlessReversal(
            GameWith(null), freshUnit, Neutral, ChinaHome, false, history));
    }
}
