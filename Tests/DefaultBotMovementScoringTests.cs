using Imperial2030.Server.Models;
using Imperial2030.Server.Services.Bots.Strategies;
using Imperial2030.Shared.Models;
using Xunit;

namespace Imperial2030.Tests;

public class DefaultBotMovementScoringTests
{
    [Fact]
    public void DefaultBotPrefersKeepingSecondArmyAtHomeOverRedundantNeutralStack()
    {
        var controller = new Player();
        var game = NewGame(controller, Nation.Brazil);
        var mover = Army(Nation.Brazil, "Manaus");
        game.Units.Add(mover);
        game.Units.Add(Army(Nation.Brazil, "Peru"));
        game.TerritoryStates.Add(new TerritoryState { TerritoryId = "Peru", Controller = Nation.Brazil });

        var strategy = new DefaultBotStrategy();

        double redundantPeruScore = strategy.ScoreManeuverDestination(game, mover, "Peru", controller);
        double stayInManausScore = strategy.ScoreManeuverDestination(game, mover, "Manaus", controller);

        Assert.True(stayInManausScore > redundantPeruScore,
            $"Expected staying ({stayInManausScore}) to beat redundant Peru reinforcement ({redundantPeruScore}).");
    }

    [Fact]
    public void DefaultBotReinforcesNeutralRegionUntilDefendersMatchReachableEnemyThreat()
    {
        var controller = new Player();
        var opponent = new Player();
        var game = NewGame(controller, Nation.USA);
        game.NationStates.Add(new NationState { Nation = Nation.Russia, ControllerId = opponent.Id });

        var mover = Army(Nation.USA, "Chicago");
        game.Units.Add(mover);
        game.Units.Add(Army(Nation.USA, "Alaska"));
        game.Units.Add(Army(Nation.Russia, "Canada"));
        game.Units.Add(Army(Nation.Russia, "Canada"));
        game.TerritoryStates.Add(new TerritoryState { TerritoryId = "Alaska", Controller = Nation.USA });

        var strategy = new DefaultBotStrategy();

        double reinforceAlaskaScore = strategy.ScoreManeuverDestination(game, mover, "Alaska", controller);
        double stayInChicagoScore = strategy.ScoreManeuverDestination(game, mover, "Chicago", controller);

        Assert.True(reinforceAlaskaScore > stayInChicagoScore,
            $"Expected needed Alaska reinforcement ({reinforceAlaskaScore}) to beat staying ({stayInChicagoScore}).");
    }

    private static Game NewGame(Player controller, Nation controlledNation) => new()
    {
        Status = GameStatus.InProgress,
        Players = new List<Player> { controller },
        NationStates = new List<NationState>
        {
            new() { Nation = controlledNation, ControllerId = controller.Id }
        },
        TerritoryStates = new List<TerritoryState>(),
        Units = new List<Unit>(),
        Actions = new List<GameAction>()
    };

    private static Unit Army(Nation nation, string territoryId) => new()
    {
        Nation = nation,
        UnitType = UnitType.Army,
        TerritoryId = territoryId
    };
}
