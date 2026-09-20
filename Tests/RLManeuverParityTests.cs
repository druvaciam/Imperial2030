using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Imperial2030.Server.Engine;
using Imperial2030.Server.Models;
using Imperial2030.Server.Services;
using Imperial2030.Server.Services.Bots;
using Imperial2030.Server.Services.Bots.Strategies;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Imperial2030.Tests;

/// <summary>
/// The maneuver decisions the RL policy is trained on must be the ones it can take in a live game.
/// Training offers, at every "where does this unit go" step, the destinations the engine allows, "stay"
/// (126) and "end the phase now" (63: the remaining units stay where they are). The live bot must offer
/// the same three kinds, or the policy answers a question with an option it was never asked in play.
/// </summary>
public class RLManeuverParityTests
{
    private static BotService BuildBotService(params IBotStrategy[] strategies)
    {
        var hub = new Mock<IHubContext<Imperial2030.Server.Hubs.GameHub>>();
        var clients = new Mock<IHubClients>();
        hub.Setup(h => h.Clients).Returns(clients.Object);
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(new Mock<IClientProxy>().Object);
        clients.Setup(c => c.All).Returns(new Mock<IClientProxy>().Object);
        return new BotService(new Mock<IServiceScopeFactory>().Object, hub.Object, strategies.ToList(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BotService>.Instance) { SkipDelays = true };
    }

    private static (Game Game, Player Bot, NationState Russia) BuildRussiaWithTwoFleetsAndAnArmy(string botType)
    {
        var bot = new Player { Id = Guid.NewGuid(), IsBot = true, BotType = botType, BotName = "Bot" };
        var russia = new NationState { Nation = Nation.Russia, ControllerId = bot.Id, RondelPosition = RondelData.ManeuverSlot1, HasMovedThisTurn = true };
        var game = new Game
        {
            Id = Guid.NewGuid(),
            Name = "Parity",
            Status = GameStatus.InProgress,
            CurrentTurnNation = Nation.Russia,
            CurrentManeuverPhase = ManeuverPhase.Fleets,
            Players = new List<Player> { bot },
            NationStates = new List<NationState> { russia },
            Units = new List<Unit>
            {
                new() { Id = Guid.NewGuid(), Nation = Nation.Russia, UnitType = UnitType.Fleet, TerritoryId = "Vladivostok" },
                new() { Id = Guid.NewGuid(), Nation = Nation.Russia, UnitType = UnitType.Fleet, TerritoryId = "Murmansk" },
                new() { Id = Guid.NewGuid(), Nation = Nation.Russia, UnitType = UnitType.Army, TerritoryId = "Moscow" }
            },
            TerritoryStates = TerritoryData.AllTerritories.Select(t => new TerritoryState { TerritoryId = t.Id }).ToList(),
            Bonds = new List<Bond>(),
            Actions = new List<GameAction>()
        };
        return (game, bot, russia);
    }

    /// <summary>Moves every unit to its first legal destination, but ends the phase before the second fleet.</summary>
    private sealed class EndsAfterFirstFleetStrategy : DefaultBotStrategy
    {
        public override string Name => "EndsAfterFirstFleet";
        public override double ScoreManeuverDestination(Game game, Unit unit, string destinationId, Player controller)
            => destinationId == unit.TerritoryId ? 0 : 1;
        public override bool EndsManeuverPhaseEarly(Game game, Unit nextUnit, Player controller)
            => nextUnit.UnitType == UnitType.Fleet && game.Units.Any(u => u.Nation == nextUnit.Nation && u.UnitType == UnitType.Fleet && u.HasMoved);
    }

    [Fact]
    public async Task ABotThatEndsThePhaseEarlyLeavesTheRemainingUnitsWhereTheyAre()
    {
        var (game, bot, _) = BuildRussiaWithTwoFleetsAndAnArmy("EndsAfterFirstFleet");
        var second = game.Units.First(u => u.TerritoryId == "Murmansk");
        var army = game.Units.First(u => u.UnitType == UnitType.Army);

        await BuildBotService(new EndsAfterFirstFleetStrategy(), new DefaultBotStrategy()).BotManeuver(null, game, game.NationStates.First(), bot);

        Assert.NotEqual("Vladivostok", game.Units.First(u => u.Id != second.Id && u.UnitType == UnitType.Fleet).TerritoryId);
        Assert.Equal("Murmansk", second.TerritoryId);
        Assert.False(second.HasMoved);
        Assert.NotEqual("Moscow", army.TerritoryId); // the Armies phase still ran
        Assert.Equal(ManeuverPhase.None, game.CurrentManeuverPhase);
    }

    /// <summary>
    /// A maneuver resumed in its Armies phase (after a battle was answered) may still have unmoved
    /// fleets - the ones an early phase end left in place. They are not moved: the Fleets phase is
    /// over. Seen live: "chose an illegal fleet move for India to IndianOcean: Not in Fleet Maneuver
    /// phase" from a resumed turn.
    /// </summary>
    [Fact]
    public async Task AResumedManeuverInTheArmiesPhaseLeavesUnmovedFleetsAlone()
    {
        var (game, bot, russia) = BuildRussiaWithTwoFleetsAndAnArmy("Default");
        game.CurrentManeuverPhase = ManeuverPhase.Armies;
        var fleets = game.Units.Where(u => u.UnitType == UnitType.Fleet).ToList();
        var army = game.Units.First(u => u.UnitType == UnitType.Army);

        await BuildBotService(new DefaultBotStrategy()).BotManeuver(null, game, russia, bot);

        Assert.All(fleets, f => Assert.False(f.HasMoved));
        Assert.True(army.HasMoved);
        Assert.Equal(ManeuverPhase.None, game.CurrentManeuverPhase);
    }

    [Fact]
    public void TheLiveManeuverMaskOffersEndPhaseStayAndTheEnginesDestinations()
    {
        var (game, bot, _) = BuildRussiaWithTwoFleetsAndAnArmy("RL-4");
        var fleet = game.Units.First(u => u.TerritoryId == "Vladivostok");

        var mask = RLBotStrategy.ManeuverMask(game, fleet, bot);

        Assert.True(mask[63], "end-phase must be offered, as in training");
        Assert.True(mask[126], "stay must be offered");
        var offered = Enumerable.Range(0, RLBotStrategy.AllManeuverTerritories.Length)
            .Where(i => mask[127 + i]).Select(i => RLBotStrategy.AllManeuverTerritories[i]).OrderBy(x => x).ToList();
        var legal = MapConnectivity.Adjacency["Vladivostok"]
            .Where(n => ManeuverEngine.FleetDestinationRefusal(game, fleet, n, bot) == null).OrderBy(x => x).ToList();
        Assert.Equal(legal, offered);
        Assert.DoesNotContain(true, mask.Take(63));
        Assert.DoesNotContain(true, mask.Skip(64).Take(62));
    }

    [Fact]
    public async Task TrainingEndPhaseGoesThroughTheEngine()
    {
        var (game, bot, _) = BuildRussiaWithTwoFleetsAndAnArmy(DeterministicRl.StrategyName);
        game.Name = "RL_Training_parity";
        bot.BotName = "RL-4Agent";
        game.Players.Add(new Player { Id = Guid.NewGuid(), IsBot = true, BotType = "Default", BotName = "Opponent" }); // the reward is relative to the best opponent
        // A fleet already holds a sea zone whose flag has not been placed: the phase end must place it (p.10, step 3).
        game.Units.Add(new Unit { Id = Guid.NewGuid(), Nation = Nation.Russia, UnitType = UnitType.Fleet, TerritoryId = "SeaOfJapan", HasMoved = true });

        // A comfortable lead. The step earns the flag it places and nothing else: a step is not worth
        // anything just for being taken while ahead.
        bot.Cash = 40;

        var server = new TcpTrainingServer(BuildBotService(new DeterministicRl(), new DefaultBotStrategy()), new Mock<ILogger<TcpTrainingServer>>().Object);
        var sessions = (ConcurrentDictionary<string, TcpTrainingServer.TrainingSession>)typeof(TcpTrainingServer)
            .GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var sessionId = Guid.NewGuid().ToString();
        sessions[sessionId] = new TcpTrainingServer.TrainingSession { Game = game, RLPlayerId = bot.Id, ManeuverSelectedTerritoryId = "Vladivostok" };
        var oldTraining = RLBotStrategy.IsTraining;
        RLBotStrategy.IsTraining = true;
        TcpTrainingServer.StepResponse? response;
        try
        {
            var step = typeof(TcpTrainingServer).GetMethod("HandleStepAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            response = await (Task<TcpTrainingServer.StepResponse?>)step.Invoke(server, [new TcpTrainingServer.TcpRequest { Command = "step", SessionId = sessionId, Action = 63 }])!;
        }
        finally
        {
            RLBotStrategy.TrainingActionOverride.Value = null;
            RLBotStrategy.IsTraining = oldTraining;
            sessions.TryRemove(sessionId, out _);
        }

        Assert.Contains(game.Actions, a => a.ActionType == "EndPhase" && a.Nation == Nation.Russia);
        Assert.Equal(TcpTrainingServer.FlagPlacementReward, response!.Reward, precision: 3);
        Assert.Equal(Nation.Russia, game.TerritoryStates.First(t => t.TerritoryId == "SeaOfJapan").Controller);
    }

    private sealed class DeterministicRl : RLBotStrategy
    {
        public const string StrategyName = "DeterministicParityRl";
        public DeterministicRl() : base(StrategyName) { }
        public override double ScoreManeuverDestination(Game game, Unit unit, string destinationId, Player controller)
            => destinationId == unit.TerritoryId ? 1_000 : -1_000;
    }
}
