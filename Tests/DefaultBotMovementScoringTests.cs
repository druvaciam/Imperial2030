using Imperial2030.Server.Models;
using Imperial2030.Server.Helpers;
using Imperial2030.Server.Services.Bots.Strategies;
using Imperial2030.Shared.Models;
using Xunit;

namespace Imperial2030.Tests;

public class DefaultBotMovementScoringTests
{
    [Theory]
    [InlineData(Nation.Brazil, "Manaus", "Colombia")]
    [InlineData(Nation.Europe, "Rome", "Turkey")]
    public void DefaultBotPrefersForwardStackOverLeavingArmyIdleAtHome(
        Nation nation,
        string homeTerritory,
        string stagingTerritory)
    {
        var controller = new Player();
        var game = NewGame(controller, nation);
        var mover = Army(nation, homeTerritory);
        game.Units.Add(mover);
        game.Units.Add(Army(nation, stagingTerritory));
        game.TerritoryStates.Add(new TerritoryState { TerritoryId = stagingTerritory, Controller = nation });

        var strategy = new DefaultBotStrategy();

        double stagingScore = strategy.ScoreManeuverDestination(game, mover, stagingTerritory, controller);
        double stayAtHomeScore = strategy.ScoreManeuverDestination(game, mover, homeTerritory, controller);

        Assert.True(stagingScore > stayAtHomeScore,
            $"Expected forward stack in {stagingTerritory} ({stagingScore}) to beat idling in {homeTerritory} ({stayAtHomeScore}).");
    }

    [Fact]
    public void DefaultBotPrefersClaimingOpenNeutralRegionOverRedundantNeutralStack()
    {
        var controller = new Player();
        var game = NewGame(controller, Nation.Brazil);
        var mover = Army(Nation.Brazil, "Manaus");
        game.Units.Add(mover);
        game.Units.Add(Army(Nation.Brazil, "Peru"));
        game.TerritoryStates.Add(new TerritoryState { TerritoryId = "Peru", Controller = Nation.Brazil });

        var strategy = new DefaultBotStrategy();

        double redundantPeruScore = strategy.ScoreManeuverDestination(game, mover, "Peru", controller);
        double openColombiaScore = strategy.ScoreManeuverDestination(game, mover, "Colombia", controller);

        Assert.True(openColombiaScore > redundantPeruScore,
            $"Expected open Colombia ({openColombiaScore}) to beat redundant Peru reinforcement ({redundantPeruScore}).");
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

        var friendlyNations = new HashSet<Nation> { Nation.USA };
        Assert.False(ManeuverDefenseHelper.IsRedundantStackMove(game, mover, "Alaska", friendlyNations));
    }

    [Fact]
    public void DefaultBotKeepsLastBerlinDefenderWhenRussianArmyCanReachIt()
    {
        var controller = new Player();
        var opponent = new Player();
        var game = NewGame(controller, Nation.Europe);
        game.Players.Add(opponent);
        game.NationStates.Add(new NationState { Nation = Nation.Russia, ControllerId = opponent.Id });

        var mover = Army(Nation.Europe, "Berlin");
        game.Units.Add(mover);
        game.Units.Add(Army(Nation.Russia, "Murmansk"));

        var strategy = new DefaultBotStrategy();

        double stayScore = strategy.ScoreManeuverDestination(game, mover, "Berlin", controller);
        double leaveForTurkeyScore = strategy.ScoreManeuverDestination(game, mover, "Turkey", controller);

        Assert.True(stayScore > leaveForTurkeyScore,
            $"Expected the last Berlin defender ({stayScore}) to outweigh staging in Turkey ({leaveForTurkeyScore}).");
    }

    [Fact]
    public void DefaultBotReleasesExcessBerlinDefenderAfterThreatIsMatched()
    {
        var controller = new Player();
        var opponent = new Player();
        var game = NewGame(controller, Nation.Europe);
        game.Players.Add(opponent);
        game.NationStates.Add(new NationState { Nation = Nation.Russia, ControllerId = opponent.Id });

        var mover = Army(Nation.Europe, "Berlin");
        game.Units.Add(mover);
        game.Units.Add(Army(Nation.Europe, "Berlin"));
        game.Units.Add(Army(Nation.Russia, "Murmansk"));

        var strategy = new DefaultBotStrategy();

        double stayScore = strategy.ScoreManeuverDestination(game, mover, "Berlin", controller);
        double leaveForTurkeyScore = strategy.ScoreManeuverDestination(game, mover, "Turkey", controller);

        Assert.True(leaveForTurkeyScore > stayScore,
            $"Expected an excess Berlin defender to be released toward Turkey ({leaveForTurkeyScore} vs {stayScore}).");
    }

    [Fact]
    public void DefaultBotReinforcesThreatenedHomeProvinceUntilThreatIsMatched()
    {
        var controller = new Player();
        var opponent = new Player();
        var game = NewGame(controller, Nation.Europe);
        game.Players.Add(opponent);
        game.NationStates.Add(new NationState { Nation = Nation.Russia, ControllerId = opponent.Id });

        var mover = Army(Nation.Europe, "Paris");
        game.Units.Add(mover);
        game.Units.Add(Army(Nation.Russia, "Murmansk"));

        var strategy = new DefaultBotStrategy();

        double reinforceBerlinScore = strategy.ScoreManeuverDestination(game, mover, "Berlin", controller);
        double stayInParisScore = strategy.ScoreManeuverDestination(game, mover, "Paris", controller);

        Assert.True(reinforceBerlinScore > stayInParisScore,
            $"Expected Berlin reinforcement ({reinforceBerlinScore}) to beat staying in Paris ({stayInParisScore}).");
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
