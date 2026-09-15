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
/// Buying a bond, passing, and answering a Swiss Bank's forced-stop question, pinned directly against
/// the engine. Imperial-2030-Rules.pdf p.11-12: the price of a bond goes into the nation's treasury; a
/// bond of the same nation may be traded in for a higher one, paying the difference; the player with the
/// highest credit sum in a nation governs it, a tie not being enough to displace the sitting government;
/// after the last investor the Investor card passes on; a Swiss Bank may force a nation whose move
/// passes Investor to stop there when its treasury can pay the interest.
/// </summary>
public class InvestorEngineTests
{
    private static (Game Game, Player A, Player B, NationState Russia) BuildGame()
    {
        var a = new Player { Id = Guid.NewGuid(), IsBot = true, BotType = "Default", BotName = "A", Cash = 20 };
        var b = new Player { Id = Guid.NewGuid(), IsBot = true, BotType = "Default", BotName = "B", Cash = 20 };
        var russia = new NationState { Nation = Nation.Russia, ControllerId = a.Id, Treasury = 0, RondelPosition = RondelData.ProductionSlot1, Power = 0 };
        var game = new Game
        {
            Id = Guid.NewGuid(),
            Name = "Investor",
            Status = GameStatus.InProgress,
            CurrentTurnNation = Nation.Russia,
            Players = new List<Player> { a, b },
            NationStates = new List<NationState> { russia },
            Units = new List<Unit>(),
            TerritoryStates = new List<TerritoryState>(),
            Bonds = new List<Bond>(),
            Actions = new List<GameAction>(),
            InvestorCardHolderId = a.Id
        };
        // A governs Russia with the 9M bond; the 2M and 12M are in the bank.
        game.Bonds.Add(new Bond { Id = Guid.NewGuid(), Nation = Nation.Russia, Cost = 9, Interest = 4, HolderId = a.Id });
        game.Bonds.Add(new Bond { Id = Guid.NewGuid(), Nation = Nation.Russia, Cost = 2, Interest = 1 });
        game.Bonds.Add(new Bond { Id = Guid.NewGuid(), Nation = Nation.Russia, Cost = 12, Interest = 5 });
        return (game, a, b, russia);
    }

    private static void OpenInvestorTurn(Game game, Player acting, params Player[] queued)
    {
        game.IsInvestorTurn = true;
        game.ActingPlayerId = acting.Id;
        game.PendingInvestorIds = queued.Select(p => p.Id).ToList();
    }

    [Fact]
    public void BuyingPaysTheTreasuryAndCanTakeTheGovernment()
    {
        var (game, a, b, russia) = BuildGame();
        OpenInvestorTurn(game, b);
        var twelve = game.Bonds.First(x => x.Cost == 12);

        var result = InvestorEngine.Buy(null, game, twelve.Id, tradeInBondId: null);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(b.Id, twelve.HolderId);
        Assert.Equal(8, b.Cash);
        Assert.Equal(12, russia.Treasury);
        Assert.Equal(b.Id, russia.ControllerId); // 12 beats A's 9 outright
        Assert.True(result.TookControl);
        Assert.Equal("A", result.PreviousControllerName);
        Assert.Equal("B", result.ActorName);
        Assert.Single(game.Actions, x => x.ActionType == "Investment");
    }

    [Fact]
    public void ATieDoesNotDisplaceTheSittingGovernment()
    {
        var (game, a, b, russia) = BuildGame();
        game.Bonds.Add(new Bond { Id = Guid.NewGuid(), Nation = Nation.Russia, Cost = 9, Interest = 4 }); // a second 9M in the bank
        OpenInvestorTurn(game, b);
        var nine = game.Bonds.First(x => x.Cost == 9 && x.HolderId == null);

        var result = InvestorEngine.Buy(null, game, nine.Id, null);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(a.Id, russia.ControllerId);
        Assert.False(result.TookControl);
    }

    [Fact]
    public void TradingInPaysOnlyTheDifferenceAndReturnsTheOldBond()
    {
        var (game, a, _, russia) = BuildGame();
        OpenInvestorTurn(game, a);
        var nine = game.Bonds.First(x => x.Cost == 9);
        var twelve = game.Bonds.First(x => x.Cost == 12);

        var result = InvestorEngine.Buy(null, game, twelve.Id, nine.Id);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(17, a.Cash);        // paid 12 - 9
        Assert.Equal(3, russia.Treasury);
        Assert.Null(nine.HolderId);
        Assert.Equal(a.Id, twelve.HolderId);
        Assert.Equal(9, result.TradeInCost);
    }

    [Fact]
    public void TradeInMustBeOwnedSameNationAndLower()
    {
        var (game, a, b, _) = BuildGame();
        OpenInvestorTurn(game, b);
        var nine = game.Bonds.First(x => x.Cost == 9);     // A's
        var twelve = game.Bonds.First(x => x.Cost == 12);
        var two = game.Bonds.First(x => x.Cost == 2);

        Assert.Equal("You do not own the trade-in bond.", InvestorEngine.Buy(null, game, twelve.Id, nine.Id).Error);

        twelve.HolderId = b.Id; // B now holds the 12M and tries to trade it in for the 2M
        Assert.Equal("New bond must be higher value.", InvestorEngine.Buy(null, game, two.Id, twelve.Id).Error);
        Assert.Equal(20, b.Cash);
        Assert.True(game.IsInvestorTurn); // nothing advanced
    }

    [Fact]
    public void ARefusedPurchaseChangesNothing()
    {
        var (game, a, b, russia) = BuildGame();
        b.Cash = 5;
        OpenInvestorTurn(game, b);
        var twelve = game.Bonds.First(x => x.Cost == 12);

        Assert.Equal("Insufficient funds.", InvestorEngine.Buy(null, game, twelve.Id, null).Error);
        Assert.Null(twelve.HolderId);
        Assert.Equal(5, b.Cash);
        Assert.Equal(0, russia.Treasury);
        Assert.Equal(a.Id, russia.ControllerId);

        var owned = game.Bonds.First(x => x.Cost == 9);
        Assert.Equal("Bond already owned.", InvestorEngine.Buy(null, game, owned.Id, null).Error);
        Assert.Equal("Bond not found.", InvestorEngine.Buy(null, game, Guid.NewGuid(), null).Error);
    }

    [Fact]
    public void TheQueueAdvancesAndTheCardPassesAfterTheLastInvestor()
    {
        var (game, a, b, _) = BuildGame();
        OpenInvestorTurn(game, a, b);

        Assert.True(InvestorEngine.Pass(null, game).Ok);
        Assert.True(game.IsInvestorTurn);
        Assert.Equal(b.Id, game.ActingPlayerId);
        Assert.Empty(game.PendingInvestorIds);

        Assert.True(InvestorEngine.Pass(null, game).Ok);
        Assert.False(game.IsInvestorTurn);
        Assert.Null(game.ActingPlayerId);
        Assert.NotEqual(a.Id, game.InvestorCardHolderId); // the card passed on
        Assert.Equal(2, game.Actions.Count(x => x.ActionType == "Investment")); // a pass is logged as an Investment entry with no bond

        Assert.Equal("Not investor turn.", InvestorEngine.Pass(null, game).Error);
    }

    // ---- Swiss Bank (p.12) ----------------------------------------------------------------------

    private static (Game Game, Player Gov, Player Bank, NationState Russia) BuildPendingForcedStop()
    {
        var (game, a, b, russia) = BuildGame();
        // B governs nothing, so B is a Swiss Bank; B also holds the 2M so the stop is worth forcing.
        game.Bonds.First(x => x.Cost == 2).HolderId = b.Id;
        russia.Treasury = 5;
        russia.RondelPosition = RondelData.ProductionSlot1;
        // The move Prod 1 -> Import (2 -> 5) crossed Investor and was stopped for B's answer.
        game.PendingSwissBankForceNation = Nation.Russia;
        game.PendingSwissBankForceTargetSlot = RondelData.ImportSlot;
        game.PendingSwissBankResponders = new List<Guid> { b.Id };
        return (game, a, b, russia);
    }

    [Fact]
    public void ForcingTheStopMovesTheNationToInvestorAndPaysInterest()
    {
        var (game, gov, bank, russia) = BuildPendingForcedStop();
        int bankCashBefore = bank.Cash;

        var result = InvestorEngine.RespondToSwissBank(null, game, bank, forceStop: true);

        Assert.True(result.Ok, result.Error);
        Assert.True(result.ForcedStop);
        Assert.True(result.MoveResolved);
        Assert.Equal(RondelData.InvestorSlot, russia.RondelPosition);
        Assert.Null(game.PendingSwissBankForceNation);
        Assert.Empty(game.PendingSwissBankResponders);
        Assert.True(russia.HasMovedThisTurn);
        // Landing on Investor pays interest: B's 2M bond earns 1M.
        Assert.Equal(bankCashBefore + 1, bank.Cash);
        Assert.Contains(game.Actions, x => x.ActionType == "SwissBankResponse" && x.Metadata.Contains("\"IsForceStop\":true"));
        Assert.Contains(game.Actions, x => x.ActionType == "Move");
    }

    [Fact]
    public void WhenEveryResponderPassesTheOriginalMoveCompletes()
    {
        var (game, gov, bank, russia) = BuildPendingForcedStop();

        var result = InvestorEngine.RespondToSwissBank(null, game, bank, forceStop: false);

        Assert.True(result.Ok, result.Error);
        Assert.False(result.ForcedStop);
        Assert.True(result.MoveResolved);
        Assert.Equal(RondelData.ImportSlot, russia.RondelPosition);
        Assert.Null(game.PendingSwissBankForceNation);
        Assert.True(game.IsInvestorTurn); // the move still crossed Investor, so the investor is activated
        Assert.Contains(game.Actions, x => x.ActionType == "SwissBankResponse" && x.Metadata.Contains("\"IsForceStop\":false"));
    }

    [Fact]
    public void APassWithRespondersRemainingResolvesNothingYet()
    {
        var (game, gov, bank, russia) = BuildPendingForcedStop();
        var second = new Player { Id = Guid.NewGuid(), IsBot = true, BotType = "Default", BotName = "C" };
        game.Players.Add(second);
        game.PendingSwissBankResponders.Add(second.Id);

        var result = InvestorEngine.RespondToSwissBank(null, game, bank, forceStop: false);

        Assert.True(result.Ok, result.Error);
        Assert.False(result.MoveResolved);
        Assert.Equal(RondelData.ProductionSlot1, russia.RondelPosition);
        Assert.Equal(Nation.Russia, game.PendingSwissBankForceNation);
        Assert.Equal(new[] { second.Id }, game.PendingSwissBankResponders);
    }

    [Fact]
    public void OnlyAListedResponderMayAnswer()
    {
        var (game, gov, bank, _) = BuildPendingForcedStop();

        Assert.Equal("You are not required to respond.", InvestorEngine.RespondToSwissBank(null, game, gov, true).Error);

        game.PendingSwissBankForceNation = null;
        Assert.Equal("No pending Swiss Bank decision.", InvestorEngine.RespondToSwissBank(null, game, bank, true).Error);
    }
}
