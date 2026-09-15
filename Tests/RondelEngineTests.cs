using System;
using System.Collections.Generic;
using System.Linq;
using Imperial2030.Server.Engine;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using Xunit;

namespace Imperial2030.Tests;

/// <summary>
/// The rondel move as one engine operation, pinned independently of the two callers that used to each
/// carry their own copy of it (GamesController.MoveNation and BotService.ExecuteBotTurn - see
/// docs/code_review.md §M3 and implementation_plan.md Phase 2).
///
/// Rules cited: Imperial-2030-Rules.pdf p.6 - the marker moves clockwise, "remaining in the same space is
/// not allowed", the first three spaces are free and each further space costs the government's player
/// money that goes to the bank; p.11 - passing the Investor space activates the investor, landing on it
/// also pays interest; p.12 - a Swiss Bank may force a stop on the Investor space when the nation could
/// pay its interest.
/// </summary>
public class RondelEngineTests
{
    private static (Game Game, NationState Ns, Player Gov, Player Other) BuildGame(int? rondelPosition, int power = 0, int cash = 10)
    {
        var gov = new Player { Id = Guid.NewGuid(), IsBot = true, BotType = "Default", BotName = "Gov", Cash = cash };
        var other = new Player { Id = Guid.NewGuid(), IsBot = true, BotType = "Default", BotName = "Other", Cash = 0 };
        var ns = new NationState { Nation = Nation.Russia, ControllerId = gov.Id, RondelPosition = rondelPosition, Power = power, Treasury = 0 };
        var game = new Game
        {
            Id = Guid.NewGuid(),
            Name = "Rondel",
            Status = GameStatus.InProgress,
            CurrentTurnNation = Nation.Russia,
            Players = new List<Player> { gov, other },
            NationStates = new List<NationState> { ns },
            Units = new List<Unit>
            {
                new() { Id = Guid.NewGuid(), Nation = Nation.Russia, UnitType = UnitType.Army, TerritoryId = "Moscow", HasMoved = true },
                new() { Id = Guid.NewGuid(), Nation = Nation.Russia, UnitType = UnitType.Fleet, TerritoryId = "Murmansk", HasMoved = true, HasConvoyed = true }
            },
            TerritoryStates = new List<TerritoryState>(),
            Bonds = new List<Bond>(),
            Actions = new List<GameAction>()
        };
        // Both players hold a Russia bond, so "other" is a bond holder but not the government.
        game.Bonds.Add(new Bond { Id = Guid.NewGuid(), Nation = Nation.Russia, Cost = 9, Interest = 4, HolderId = gov.Id });
        game.Bonds.Add(new Bond { Id = Guid.NewGuid(), Nation = Nation.Russia, Cost = 2, Interest = 1, HolderId = other.Id });
        return (game, ns, gov, other);
    }

    [Fact]
    public void FirstPlacementIsFreeAndMarksTheNationMoved()
    {
        var (game, ns, gov, _) = BuildGame(rondelPosition: null, cash: 3);

        var result = RondelEngine.MoveNation(null, game, Nation.Russia, RondelData.ProductionSlot2);

        Assert.True(result.Ok, result.Error);
        Assert.False(result.SwissBankIntercepted);
        Assert.Null(result.PreviousSlot);
        Assert.Equal(0, result.Cost);
        Assert.Equal(3, gov.Cash);
        Assert.Equal(RondelData.ProductionSlot2, ns.RondelPosition);
        Assert.True(ns.HasMovedThisTurn);
        Assert.All(game.Units, u => { Assert.False(u.HasMoved); Assert.False(u.HasConvoyed); });
        Assert.Single(game.Actions, a => a.ActionType == "Move" && a.PlayerName == "Gov");
    }

    [Fact]
    public void FourthSpaceOnwardsIsPaidByTheGovernmentsPlayer()
    {
        // p.6: three spaces free. From slot 0 to slot 5 is five spaces: two paid, at 1M each at power 0.
        var (game, ns, gov, _) = BuildGame(rondelPosition: 0, power: 0, cash: 10);

        var result = RondelEngine.MoveNation(null, game, Nation.Russia, RondelData.ImportSlot);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(0, result.PreviousSlot);
        Assert.Equal(2, result.Cost);
        Assert.Equal(8, gov.Cash);
        Assert.Equal(RondelData.ImportSlot, ns.RondelPosition);
    }

    [Fact]
    public void AnUnaffordableMoveIsRejectedAndNothingChanges()
    {
        var (game, ns, gov, _) = BuildGame(rondelPosition: 0, power: 0, cash: 1);

        var result = RondelEngine.MoveNation(null, game, Nation.Russia, RondelData.ImportSlot);

        Assert.False(result.Ok);
        Assert.Equal("Not enough cash. Cost: 2M", result.Error);
        Assert.Equal(1, gov.Cash);
        Assert.Equal(0, ns.RondelPosition);
        Assert.False(ns.HasMovedThisTurn);
        Assert.Empty(game.Actions);
    }

    [Theory]
    [InlineData(Nation.China, "It is Russia's turn.")]
    public void TheWrongNationCannotMove(Nation nation, string expected)
    {
        var (game, _, _, _) = BuildGame(rondelPosition: 0);
        var result = RondelEngine.MoveNation(null, game, nation, 1);
        Assert.False(result.Ok);
        Assert.Equal(expected, result.Error);
    }

    [Fact]
    public void StayingOnTheSameSpaceIsNotAllowed()
    {
        // p.6: "remaining in the same space is not allowed".
        var (game, _, _, _) = BuildGame(rondelPosition: 2);
        var result = RondelEngine.MoveNation(null, game, Nation.Russia, 2);
        Assert.False(result.Ok);
        Assert.Equal("Must move to a different slot.", result.Error);
    }

    [Fact]
    public void MovingSevenSpacesIsBeyondTheMaximum()
    {
        var (game, _, _, _) = BuildGame(rondelPosition: 1, cash: 100);
        var result = RondelEngine.MoveNation(null, game, Nation.Russia, 0); // 1 -> 0 clockwise is 7 spaces
        Assert.False(result.Ok);
        Assert.Equal($"Cannot move more than {RondelData.MaxMoveDistance} spaces on the rondel.", result.Error);
    }

    [Fact]
    public void ANationThatAlreadyMovedThisTurnCannotMoveAgain()
    {
        var (game, ns, _, _) = BuildGame(rondelPosition: 0);
        ns.HasMovedThisTurn = true;
        var result = RondelEngine.MoveNation(null, game, Nation.Russia, 1);
        Assert.False(result.Ok);
        Assert.Equal("Already moved this turn.", result.Error);
    }

    [Fact]
    public void LandingOnInvestorActivatesTheInvestorPhase()
    {
        // p.11: the investor card holder is activated. Give "other" the card so the phase has someone to act.
        var (game, ns, _, other) = BuildGame(rondelPosition: RondelData.ProductionSlot1);
        game.InvestorCardHolderId = other.Id;

        var result = RondelEngine.MoveNation(null, game, Nation.Russia, RondelData.InvestorSlot);

        Assert.True(result.Ok, result.Error);
        Assert.True(game.IsInvestorTurn);
        Assert.Equal(other.Id, game.ActingPlayerId);
        Assert.Contains(game.Actions, a => a.ActionType == "InvestorBonus");
        Assert.Equal(ManeuverPhase.None, game.CurrentManeuverPhase);
    }

    [Fact]
    public void PassingOverInvestorWithASwissBankAbleToForceAStopIsIntercepted()
    {
        // p.12: a Swiss Bank (a player governing no nation) may force the nation to stop on Investor when its
        // treasury can pay the interest. "other" governs nothing, and treasury 5 covers 4 + 1 interest.
        var (game, ns, gov, other) = BuildGame(rondelPosition: RondelData.ProductionSlot1, cash: 10);
        ns.Treasury = 5;

        var result = RondelEngine.MoveNation(null, game, Nation.Russia, RondelData.ImportSlot); // 2 -> 5 crosses 4

        Assert.True(result.Ok, result.Error);
        Assert.True(result.SwissBankIntercepted);
        // Nothing moved: the decision now belongs to the responders.
        Assert.Equal(RondelData.ProductionSlot1, ns.RondelPosition);
        Assert.False(ns.HasMovedThisTurn);
        Assert.Equal(10, gov.Cash);
        Assert.Empty(game.Actions);
        Assert.Equal(Nation.Russia, game.PendingSwissBankForceNation);
        Assert.Equal(RondelData.ImportSlot, game.PendingSwissBankForceTargetSlot);
        Assert.Equal(new[] { other.Id }, game.PendingSwissBankResponders);
    }

    [Fact]
    public void PassingOverInvestorWhenTheTreasuryCannotPayIsNotIntercepted()
    {
        var (game, ns, _, _) = BuildGame(rondelPosition: RondelData.ProductionSlot1, cash: 10);
        ns.Treasury = 4; // owes 5

        var result = RondelEngine.MoveNation(null, game, Nation.Russia, RondelData.ImportSlot);

        Assert.True(result.Ok, result.Error);
        Assert.False(result.SwissBankIntercepted);
        Assert.Equal(RondelData.ImportSlot, ns.RondelPosition);
    }

    [Fact]
    public void LandingOnManeuverStartsTheFleetsPhaseAndEmptyPhasesCanBeSkipped()
    {
        var (game, ns, gov, _) = BuildGame(rondelPosition: RondelData.TaxationSlot);
        game.Units.Clear();

        var result = RondelEngine.MoveNation(null, game, Nation.Russia, RondelData.ManeuverSlot1);
        Assert.True(result.Ok, result.Error);
        Assert.Equal(ManeuverPhase.Fleets, game.CurrentManeuverPhase);

        RondelEngine.AutoSkipEmptyManeuverPhases(null, game, Nation.Russia, "Gov");

        Assert.Equal(ManeuverPhase.None, game.CurrentManeuverPhase);
        Assert.Equal(2, game.Actions.Count(a => a.ActionType == "AutoSkipPhase"));
    }
}
