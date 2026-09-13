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
        var strategy = new DefaultBotStrategy();

        var preview = TaxationHelper.PreviewTaxation(game, nationState);
        double taxationScore = strategy.ScoreRondelSlot(
            RondelData.TaxationSlot, game, nationState, controller, factories: 4, units: 6);
        double productionScore = strategy.ScoreRondelSlot(
            RondelData.ProductionSlot1, game, nationState, controller, factories: 4, units: 6);

        Assert.Equal(10, preview.ExpectedPowerGain);
        Assert.Equal(7, preview.ExpectedTreasuryGain);
        Assert.Equal(5, preview.ExpectedBonus);
        Assert.True(taxationScore > productionScore,
            $"Expected maximum Taxation ({taxationScore}) to beat Production ({productionScore}).");

        double taxationWeight = BotService.GetRondelSelectionWeight(strategy, taxationScore, taxationScore);
        double productionWeight = BotService.GetRondelSelectionWeight(strategy, productionScore, taxationScore);

        Assert.True(productionWeight > 0, "Production must remain possible so Default's choice is still random.");
        Assert.True(taxationWeight >= productionWeight * 20,
            $"Expected maximum Taxation weight ({taxationWeight}) to be at least 20x Production ({productionWeight}).");

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
}
