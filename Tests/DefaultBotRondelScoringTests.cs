using Imperial2030.Server.Helpers;
using Imperial2030.Server.Models;
using Imperial2030.Server.Services;
using Imperial2030.Server.Services.Bots.Strategies;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using Xunit;

namespace Imperial2030.Tests;

public class DefaultBotRondelScoringTests
{
    [Fact]
    public void ProductionScoresZeroAtFactoriesPlusThreeUnits()
    {
        var controller = new Player { Cash = 10, IsBot = true, BotType = "Default" };
        var nationState = new NationState
        {
            Nation = Nation.Europe,
            ControllerId = controller.Id,
            Treasury = 10
        };
        var game = NewGame(controller, new Player(), nationState);
        foreach (string territoryId in new[] { "Berlin", "Paris", "Rome" })
        {
            game.TerritoryStates.Add(new TerritoryState { TerritoryId = territoryId, HasFactory = true });
        }

        double production = new DefaultBotStrategy().ScoreRondelSlot(
            RondelData.ProductionSlot1,
            game,
            nationState,
            controller,
            factories: 3,
            units: 6);
        Assert.Equal(0, production);
    }

    [Fact]
    public void DefaultBotGivesNearbyMaximumTaxationVeryHighWeight()
    {
        var controller = new Player { Cash = 10, IsBot = true, BotType = "Default" };
        var nationState = new NationState
        {
            Nation = Nation.India,
            ControllerId = controller.Id,
            RondelPosition = RondelData.ManeuverSlot2,
            Treasury = 10,
            Power = 0
        };
        var game = NewMaximumTaxGame(controller, nationState);
        foreach (var enemy in Enumerable.Range(0, 6).Select(_ => Army(Nation.China, "Urumqi")))
        {
            game.Units.Add(enemy);
        }
        var strategy = new DefaultBotStrategy();

        var preview = TaxationHelper.PreviewTaxation(game, nationState);
        double taxationScore = strategy.ScoreRondelSlot(
            RondelData.TaxationSlot, game, nationState, controller, factories: 4, units: 6);
        double productionScore = strategy.ScoreRondelSlot(
            RondelData.ProductionSlot1, game, nationState, controller, factories: 4, units: 6);
        double importScore = strategy.ScoreRondelSlot(
            RondelData.ImportSlot, game, nationState, controller, factories: 4, units: 6);

        Assert.Equal(10, preview.ExpectedPowerGain);
        Assert.Equal(7, preview.ExpectedTreasuryGain);
        Assert.Equal(5, preview.ExpectedBonus);
        Assert.True(taxationScore > productionScore,
            $"Expected maximum Taxation ({taxationScore}) to beat Production ({productionScore}).");
        Assert.True(taxationScore > importScore,
            $"Expected maximum Taxation ({taxationScore}) to beat emergency Import ({importScore}).");

        double taxationWeight = BotService.GetRondelSelectionWeight(strategy, taxationScore, taxationScore);
        double productionWeight = BotService.GetRondelSelectionWeight(strategy, productionScore, taxationScore);

        Assert.True(productionWeight > 0, "Production must remain possible so Default's choice is still random.");
        Assert.True(taxationWeight >= productionWeight * 20,
            $"Expected maximum Taxation weight ({taxationWeight}) to be at least 20x Production ({productionWeight}).");
        double importWeight = BotService.GetRondelSelectionWeight(strategy, importScore, taxationScore);
        Assert.True(taxationWeight >= importWeight * 20,
            $"Expected maximum Taxation weight ({taxationWeight}) to be at least 20x emergency Import ({importWeight}).");

        double maneuverScoreAfterCost = strategy.ScoreRondelSlot(
            RondelData.ManeuverSlot1, game, nationState, controller, factories: 4, units: 6)
            - RondelData.GetMoveCost(nationState.RondelPosition, RondelData.ManeuverSlot1, nationState.Power) * 2;
        double importScoreAfterCost = strategy.ScoreRondelSlot(
            RondelData.ImportSlot, game, nationState, controller, factories: 4, units: 6)
            - RondelData.GetMoveCost(nationState.RondelPosition, RondelData.ImportSlot, nationState.Power) * 2;
        double taxationProbability = taxationWeight / new[]
        {
            taxationWeight,
            productionWeight,
            BotService.GetRondelSelectionWeight(strategy, maneuverScoreAfterCost, taxationScore),
            BotService.GetRondelSelectionWeight(strategy, importScoreAfterCost, taxationScore)
        }.Sum();

        Assert.InRange(taxationProbability, 0.95, 1.0);
    }

    [Fact]
    public void HomeDangerRaisesProductionAboveOrdinaryManeuver()
    {
        var controller = new Player { Cash = 10, IsBot = true, BotType = "Default" };
        var opponent = new Player();
        var nationState = new NationState { Nation = Nation.Europe, ControllerId = controller.Id, Treasury = 5 };
        var game = NewGame(controller, opponent, nationState);
        game.NationStates.Add(new NationState { Nation = Nation.Russia, ControllerId = opponent.Id });
        game.TerritoryStates.Add(new TerritoryState { TerritoryId = "Berlin", HasFactory = true });
        game.Units.Add(Army(Nation.Russia, "Murmansk"));

        var strategy = new DefaultBotStrategy();
        double production = strategy.ScoreRondelSlot(RondelData.ProductionSlot1, game, nationState, controller, 1, 0);
        double maneuver = strategy.ScoreRondelSlot(RondelData.ManeuverSlot1, game, nationState, controller, 1, 0);

        Assert.True(production > maneuver, $"Expected threatened Production ({production}) to beat Maneuver ({maneuver}).");
    }

    [Fact]
    public void OccupationAndLimitedFactoriesRaiseAffordableImportAboveProduction()
    {
        var controller = new Player { Cash = 10, IsBot = true, BotType = "Default" };
        var opponent = new Player();
        var nationState = new NationState { Nation = Nation.Europe, ControllerId = controller.Id, Treasury = 3 };
        var game = NewGame(controller, opponent, nationState);
        game.NationStates.Add(new NationState { Nation = Nation.Russia, ControllerId = opponent.Id });
        game.TerritoryStates.Add(new TerritoryState { TerritoryId = "Berlin", HasFactory = true });
        game.TerritoryStates.Add(new TerritoryState { TerritoryId = "Paris", HasFactory = true });
        game.Units.Add(new Unit
        {
            Nation = Nation.Russia,
            UnitType = UnitType.Army,
            TerritoryId = "Berlin",
            IsHostile = true
        });

        var strategy = new DefaultBotStrategy();
        double production = strategy.ScoreRondelSlot(RondelData.ProductionSlot1, game, nationState, controller, 2, 0);
        double import = strategy.ScoreRondelSlot(RondelData.ImportSlot, game, nationState, controller, 2, 0);

        Assert.True(import > production, $"Expected emergency Import ({import}) to beat Production ({production}).");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void OneOrTwoUsableFactoriesUnderThreatRaiseImportAboveProduction(int factoryCount)
    {
        var controller = new Player { Cash = 10, IsBot = true, BotType = "Default" };
        var opponent = new Player();
        var nationState = new NationState { Nation = Nation.Europe, ControllerId = controller.Id, Treasury = 3 };
        var game = NewGame(controller, opponent, nationState);
        game.NationStates.Add(new NationState { Nation = Nation.Russia, ControllerId = opponent.Id });
        foreach (string territoryId in new[] { "Berlin", "Paris" }.Take(factoryCount))
        {
            game.TerritoryStates.Add(new TerritoryState { TerritoryId = territoryId, HasFactory = true });
        }
        game.Units.Add(Army(Nation.Russia, "Murmansk"));

        var strategy = new DefaultBotStrategy();
        double production = strategy.ScoreRondelSlot(RondelData.ProductionSlot1, game, nationState, controller, factoryCount, 0);
        double import = strategy.ScoreRondelSlot(RondelData.ImportSlot, game, nationState, controller, factoryCount, 0);

        Assert.True(import > production, $"Expected Import ({import}) to beat {factoryCount}-factory Production ({production}).");
    }

    [Fact]
    public void InsufficientArmamentCapacityUnderLandThreatRaisesImportAboveProduction()
    {
        var controller = new Player { Cash = 10, IsBot = true, BotType = "Default" };
        var opponent = new Player();
        var nationState = new NationState { Nation = Nation.Europe, ControllerId = controller.Id, Treasury = 3 };
        var game = NewGame(controller, opponent, nationState);
        game.NationStates.Add(new NationState { Nation = Nation.Russia, ControllerId = opponent.Id });
        foreach (string territoryId in new[] { "Berlin", "Paris", "Rome", "London" })
        {
            game.TerritoryStates.Add(new TerritoryState { TerritoryId = territoryId, HasFactory = true });
        }
        foreach (var enemy in Enumerable.Range(0, 3).Select(_ => Army(Nation.Russia, "Murmansk")))
        {
            game.Units.Add(enemy);
        }

        var strategy = new DefaultBotStrategy();
        double production = strategy.ScoreRondelSlot(RondelData.ProductionSlot1, game, nationState, controller, 4, 0);
        double import = strategy.ScoreRondelSlot(RondelData.ImportSlot, game, nationState, controller, 4, 0);

        Assert.True(import > production, $"Expected army-focused Import ({import}) to beat Production ({production}).");
    }

    [Theory]
    [InlineData(RondelData.InvestorSlot)]
    [InlineData(RondelData.ManeuverSlot1)]
    [InlineData(RondelData.ProductionSlot1)]
    public void PaidFourthThroughSixthRondelMovesLoseWeightForDefaultBot(int startingSlot)
    {
        var controller = new Player { Cash = 20, IsBot = true, BotType = "Default" };
        var nationState = new NationState
        {
            Nation = Nation.Europe,
            ControllerId = controller.Id,
            RondelPosition = startingSlot,
            Treasury = 3,
            Power = 10
        };
        var game = NewGame(controller, new Player(), nationState);
        var strategy = new DefaultBotStrategy();

        double raw = strategy.ScoreRondelSlot(RondelData.TaxationSlot, game, nationState, controller, 0, 0);
        double adjusted = BotService.GetAdjustedRondelCandidateScore(
            strategy, RondelData.TaxationSlot, game, nationState, controller, 0, 0);

        Assert.Equal(raw - RondelData.GetMoveCost(nationState.RondelPosition, RondelData.TaxationSlot, nationState.Power) * 2, adjusted);
        Assert.True(adjusted < raw);
        Assert.True(
            BotService.GetRondelSelectionWeight(strategy, adjusted, raw)
            < BotService.GetRondelSelectionWeight(strategy, raw, raw));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnlyProjectedWinnerIgnoresCostOfGameEndingTaxation(bool controllerWillWin)
    {
        var controller = new Player { Cash = controllerWillWin ? 20 : 5, IsBot = true, BotType = "Default" };
        var opponent = new Player { Cash = controllerWillWin ? 0 : 100 };
        var nationState = new NationState
        {
            Nation = Nation.Europe,
            ControllerId = controller.Id,
            RondelPosition = RondelData.InvestorSlot,
            Treasury = 10,
            Power = 24
        };
        var game = NewGame(controller, opponent, nationState);
        foreach (string territoryId in new[] { "Berlin", "Paris", "Rome" })
        {
            game.TerritoryStates.Add(new TerritoryState { TerritoryId = territoryId, HasFactory = true });
        }

        var strategy = new DefaultBotStrategy();
        double raw = strategy.ScoreRondelSlot(RondelData.TaxationSlot, game, nationState, controller, 3, 0);
        double adjusted = BotService.GetAdjustedRondelCandidateScore(
            strategy, RondelData.TaxationSlot, game, nationState, controller, 3, 0);

        if (controllerWillWin)
            Assert.Equal(raw, adjusted);
        else
            Assert.Equal(raw - RondelData.GetMoveCost(nationState.RondelPosition, RondelData.TaxationSlot, nationState.Power) * 2, adjusted);
    }

    [Fact]
    public void GameEndingTaxationStillPaysMoveCostWhenProjectingWinner()
    {
        var controller = new Player { Cash = 10, IsBot = true, BotType = "Default" };
        var opponent = new Player { Cash = 8 };
        var nationState = new NationState
        {
            Nation = Nation.Europe,
            ControllerId = controller.Id,
            RondelPosition = RondelData.InvestorSlot,
            Treasury = 10,
            Power = 24
        };
        var game = NewGame(controller, opponent, nationState);
        foreach (string territoryId in new[] { "Berlin", "Paris", "Rome" })
        {
            game.TerritoryStates.Add(new TerritoryState { TerritoryId = territoryId, HasFactory = true });
        }

        var strategy = new DefaultBotStrategy();
        double raw = strategy.ScoreRondelSlot(RondelData.TaxationSlot, game, nationState, controller, 3, 0);
        double adjusted = BotService.GetAdjustedRondelCandidateScore(
            strategy, RondelData.TaxationSlot, game, nationState, controller, 3, 0);

        int moveCost = RondelData.GetMoveCost(nationState.RondelPosition, RondelData.TaxationSlot, nationState.Power);
        Assert.Equal(5, moveCost);
        Assert.Equal(raw - moveCost * 2, adjusted);
    }

    private static Game NewMaximumTaxGame(Player controller, NationState nationState)
    {
        var homeTerritories = TerritoryData.AllTerritories
            .Where(territory => territory.Nation == Nation.India)
            .ToList();
        var flagTerritories = TerritoryData.AllTerritories
            .Where(territory => territory.Nation == null)
            .Take(10)
            .ToList();

        return new Game
        {
            Status = GameStatus.InProgress,
            Players = new List<Player> { controller },
            NationStates = new List<NationState> { nationState },
            TerritoryStates = homeTerritories
                .Select(territory => new TerritoryState
                {
                    TerritoryId = territory.Id,
                    HasFactory = true
                })
                .Concat(flagTerritories.Select(territory => new TerritoryState
                {
                    TerritoryId = territory.Id,
                    Controller = Nation.India
                }))
                .ToList(),
            Units = Enumerable.Range(0, 6)
                .Select(_ => new Unit
                {
                    Nation = Nation.India,
                    UnitType = UnitType.Army,
                    TerritoryId = "Delhi"
                })
                .ToList(),
            Actions = new List<GameAction>()
        };
    }

    private static Game NewGame(Player controller, Player opponent, NationState nationState) => new()
    {
        Status = GameStatus.InProgress,
        Players = new List<Player> { controller, opponent },
        NationStates = new List<NationState> { nationState },
        TerritoryStates = new List<TerritoryState>(),
        Units = new List<Unit>(),
        Bonds = new List<Bond>(),
        Actions = new List<GameAction>()
    };

    private static Unit Army(Nation nation, string territoryId) => new()
    {
        Nation = nation,
        UnitType = UnitType.Army,
        TerritoryId = territoryId
    };
}
