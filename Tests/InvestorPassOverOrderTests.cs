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
/// Imperial-2030-Rules.pdf p.11: "Steps two and three are also executed when the 'Investor' space on the
/// rondel is not landed on but is only passed over. In this second case, the action determined by the
/// space landed on is completed first." So on a pass-over the nation acts first, the Investor turn opens
/// when that action is complete, and the rotation moves on when the Investor turn ends. Landing on
/// Investor is unchanged: the Investor turn IS the action.
/// </summary>
public class InvestorPassOverOrderTests
{
    private static (Game Game, Player Gov, Player CardHolder, NationState Russia, NationState China) BuildGame()
    {
        var gov = new Player { Id = Guid.NewGuid(), IsBot = true, BotType = "Default", BotName = "Gov", Cash = 20 };
        var holder = new Player { Id = Guid.NewGuid(), IsBot = true, BotType = "Default", BotName = "Holder", Cash = 20 };
        var russia = new NationState { Nation = Nation.Russia, ControllerId = gov.Id, Treasury = 10, RondelPosition = RondelData.ManeuverSlot1, Power = 0 };
        var china = new NationState { Nation = Nation.China, ControllerId = holder.Id, Treasury = 10, RondelPosition = RondelData.TaxationSlot, Power = 0 };
        var game = new Game
        {
            Id = Guid.NewGuid(),
            Name = "Pass-over",
            Status = GameStatus.InProgress,
            CurrentTurnNation = Nation.Russia,
            Players = new List<Player> { gov, holder },
            NationStates = new List<NationState> { russia, china },
            Units = new List<Unit>(),
            TerritoryStates = TerritoryData.AllTerritories.Select(t => new TerritoryState { TerritoryId = t.Id }).ToList(),
            Bonds = new List<Bond>(),
            Actions = new List<GameAction>(),
            InvestorCardHolderId = holder.Id
        };
        game.Bonds.Add(new Bond { Id = Guid.NewGuid(), Nation = Nation.Russia, Cost = 9, Interest = 4, HolderId = gov.Id });
        game.Bonds.Add(new Bond { Id = Guid.NewGuid(), Nation = Nation.Russia, Cost = 12, Interest = 5 });
        game.Bonds.Add(new Bond { Id = Guid.NewGuid(), Nation = Nation.China, Cost = 9, Interest = 4, HolderId = holder.Id });
        return (game, gov, holder, russia, china);
    }

    [Fact]
    public void APassOverDoesNotOpenTheInvestorTurnBeforeTheAction()
    {
        var (game, gov, holder, russia, _) = BuildGame();

        // Maneuver 1 -> Import passes Investor.
        var move = RondelEngine.MoveNation(null, game, Nation.Russia, RondelData.ImportSlot);

        Assert.True(move.Ok, move.Error);
        Assert.False(game.IsInvestorTurn);
        Assert.Null(game.ActingPlayerId);
        Assert.Equal(20, holder.Cash); // the 2M investor bonus is not paid until the Investor is activated
        Assert.True(game.InvestorTurnPending);
        Assert.Equal(RondelData.ImportSlot, russia.RondelPosition);

        // The government acts on the space it landed on.
        var import = ImportEngine.Import(null, game, new List<(UnitType, string)> { (UnitType.Army, TerritoryData.AllTerritories.First(t => t.Nation == Nation.Russia).Id) });
        Assert.True(import.Ok, import.Error);
    }

    [Fact]
    public void EndingTheTurnOpensThePassedOverInvestorTurnAndTheRotationMovesOnWhenItEnds()
    {
        var (game, gov, holder, russia, _) = BuildGame();
        Assert.True(RondelEngine.MoveNation(null, game, Nation.Russia, RondelData.ImportSlot).Ok);

        var end = TurnEngine.EndTurn(null, game);

        Assert.True(end.Ok, end.Error);
        Assert.True(game.IsInvestorTurn);
        Assert.Equal(holder.Id, game.ActingPlayerId);
        Assert.Equal(22, holder.Cash); // 2M bonus on activation (p.11 step 2)
        Assert.Equal(Nation.Russia, game.CurrentTurnNation); // the rotation has not moved yet
        Assert.DoesNotContain(game.Actions, a => a.ActionType == "EndTurn");

        var pass = InvestorEngine.Pass(null, game);

        Assert.True(pass.Ok, pass.Error);
        Assert.False(game.IsInvestorTurn);
        Assert.False(game.InvestorTurnPending);
        Assert.Equal(Nation.China, game.CurrentTurnNation);
        Assert.False(russia.HasMovedThisTurn);
        Assert.Single(game.Actions, a => a.ActionType == "EndTurn" && a.Nation == Nation.Russia);
        Assert.Equal(gov.Id, game.InvestorCardHolderId); // the card passed on
    }

    [Fact]
    public void ANewGovernmentTakingOverDuringThePassedOverInvestorTurnGetsNoAction()
    {
        var (game, gov, holder, russia, _) = BuildGame();
        Assert.True(RondelEngine.MoveNation(null, game, Nation.Russia, RondelData.ImportSlot).Ok);
        Assert.True(TurnEngine.EndTurn(null, game).Ok);

        // The card holder buys Russia's 12M and outbids the 9M government.
        var twelve = game.Bonds.First(b => b.Nation == Nation.Russia && b.Cost == 12);
        var buy = InvestorEngine.Buy(null, game, twelve.Id, tradeInBondId: null);

        Assert.True(buy.Ok, buy.Error);
        Assert.True(buy.TookControl);
        Assert.Equal(holder.Id, russia.ControllerId);
        // Russia's action was already completed before the Investor turn, so its turn is over.
        Assert.False(game.IsInvestorTurn);
        Assert.Equal(Nation.China, game.CurrentTurnNation);
    }

    [Fact]
    public void TaxationOpensThePassedOverInvestorTurnBeforeTheRotationMovesOn()
    {
        var (game, gov, holder, russia, _) = BuildGame();
        russia.RondelPosition = RondelData.ManeuverSlot1;
        russia.Power = 0;
        // Maneuver 1 -> Taxation is five spaces; the government pays for the fourth and fifth.
        Assert.True(RondelEngine.MoveNation(null, game, Nation.Russia, RondelData.TaxationSlot).Ok);

        var tax = TaxationEngine.ExecuteTaxation(null, game);

        Assert.True(tax.Ok, tax.Error);
        Assert.True(game.IsInvestorTurn);
        Assert.Equal(Nation.Russia, game.CurrentTurnNation);
        Assert.Contains(game.Actions, a => a.ActionType == "Taxation");

        Assert.True(InvestorEngine.Pass(null, game).Ok);
        Assert.Equal(Nation.China, game.CurrentTurnNation);
    }

    [Fact]
    public void WithNobodyToActivateTheTurnEndsAtOnce()
    {
        var (game, gov, holder, russia, china) = BuildGame();
        game.InvestorCardHolderId = null; // and both players govern a nation, so there is no Swiss Bank
        Assert.True(RondelEngine.MoveNation(null, game, Nation.Russia, RondelData.ImportSlot).Ok);
        Assert.True(game.InvestorTurnPending);

        var end = TurnEngine.EndTurn(null, game);

        Assert.True(end.Ok, end.Error);
        Assert.False(game.IsInvestorTurn);
        Assert.False(game.InvestorTurnPending);
        Assert.Equal(Nation.China, game.CurrentTurnNation);
    }

    [Fact]
    public void LandingOnInvestorStillOpensTheInvestorTurnAtOnce()
    {
        var (game, gov, holder, russia, _) = BuildGame();

        Assert.True(RondelEngine.MoveNation(null, game, Nation.Russia, RondelData.InvestorSlot).Ok);

        Assert.True(game.IsInvestorTurn);
        Assert.False(game.InvestorTurnPending);
        Assert.Equal(holder.Id, game.ActingPlayerId);
        Assert.Equal(6, russia.Treasury); // interest paid: 4M to the government (p.11 step 1)
        Assert.Equal(22, holder.Cash);

        // The Investor turn was the action; ending the nation's turn stays the government's call.
        Assert.True(InvestorEngine.Pass(null, game).Ok);
        Assert.Equal(Nation.Russia, game.CurrentTurnNation);
        Assert.True(TurnEngine.EndTurn(null, game).Ok);
        Assert.Equal(Nation.China, game.CurrentTurnNation);
    }

    [Fact]
    public void ThePendingInvestorTurnDoesNotStopTheGovernmentFromEndingItsTurn()
    {
        var (game, gov, holder, russia, _) = BuildGame();
        Assert.True(RondelEngine.MoveNation(null, game, Nation.Russia, RondelData.ImportSlot).Ok);

        // Nothing imported: the turn can still be ended, and the Investor turn then opens.
        Assert.True(TurnEngine.EndTurn(null, game).Ok);
        Assert.True(game.IsInvestorTurn);

        // While it is open, the nation's turn cannot be ended again.
        var again = TurnEngine.EndTurn(null, game);
        Assert.False(again.Ok);
    }
}
