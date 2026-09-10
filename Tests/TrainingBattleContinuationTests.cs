using System.Collections.Concurrent;
using System.Reflection;
using Imperial2030.Server.Models;
using Imperial2030.Server.Services;
using Imperial2030.Server.Services.Bots.Strategies;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Imperial2030.Tests;

public sealed class TrainingStallRegressionTests
{
    [Fact]
    public async Task SwissBankForceStopToInvestor_EstablishesInvestorActionPhase()
    {
        var gameId = Guid.NewGuid();
        var controllerId = Guid.NewGuid();
        var swissBankId = Guid.NewGuid();
        var game = new Game
        {
            Id = gameId,
            Name = "RL_Training_swiss_bank_phase",
            Status = GameStatus.InProgress,
            CurrentTurnNation = Nation.Russia,
            // This is the contradictory phase captured in the halted session. Resolving the deferred move
            // to Investor must establish Investor's phase (None), irrespective of the interrupted value.
            CurrentManeuverPhase = ManeuverPhase.Fleets,
            InvestorCardHolderId = swissBankId,
            Players = new List<Player>
            {
                new() { Id = controllerId, GameId = gameId, IsBot = true, BotName = "Russia bot", BotType = "Default" },
                new() { Id = swissBankId, GameId = gameId, IsBot = true, BotName = "Swiss bank", BotType = "Default" }
            },
            NationStates = new List<NationState>
            {
                new()
                {
                    GameId = gameId, Nation = Nation.Russia, ControllerId = controllerId,
                    RondelPosition = RondelData.ManeuverSlot1, Treasury = 20
                }
            },
            Bonds = new List<Bond>
            {
                new() { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.Russia, Cost = 2, Interest = 1, HolderId = swissBankId }
            },
            PendingSwissBankForceNation = Nation.Russia,
            PendingSwissBankForceTargetSlot = RondelData.ManeuverSlot2,
            PendingSwissBankResponders = new List<Guid> { swissBankId }
        };

        var botService = CreateBotService();

        await botService.HandleBotSwissBankResponse(null, game, [game.Players.Single(p => p.Id == swissBankId)]);

        Assert.Equal(RondelData.InvestorSlot, game.NationStates.Single().RondelPosition);
        Assert.Equal(ManeuverPhase.None, game.CurrentManeuverPhase);
    }

    [Fact]
    public async Task SwissBankPassToManeuver_InitializesTheDeferredManeuverPhase()
    {
        var gameId = Guid.NewGuid();
        var controllerId = Guid.NewGuid();
        var swissBankId = Guid.NewGuid();
        var game = new Game
        {
            Id = gameId,
            Name = "RL_Training_swiss_bank_pass",
            Status = GameStatus.InProgress,
            CurrentTurnNation = Nation.Russia,
            CurrentManeuverPhase = ManeuverPhase.None,
            InvestorCardHolderId = controllerId,
            Players = new List<Player>
            {
                new() { Id = controllerId, GameId = gameId, IsBot = true, BotName = "Russia bot", BotType = "Default" },
                new() { Id = swissBankId, GameId = gameId, IsBot = true, BotName = "Swiss bank", BotType = "Default" }
            },
            NationStates = new List<NationState>
            {
                new()
                {
                    GameId = gameId, Nation = Nation.Russia, ControllerId = controllerId,
                    RondelPosition = RondelData.ManeuverSlot1, Treasury = 20
                }
            },
            // The Swiss Bank owns no Russian bond, so the default strategy passes instead of forcing.
            Bonds = new List<Bond>(),
            PendingSwissBankForceNation = Nation.Russia,
            PendingSwissBankForceTargetSlot = RondelData.ManeuverSlot2,
            PendingSwissBankResponders = new List<Guid> { swissBankId }
        };

        var botService = CreateBotService();

        await botService.HandleBotSwissBankResponse(null, game, [game.Players.Single(p => p.Id == swissBankId)]);

        Assert.Equal(RondelData.ManeuverSlot2, game.NationStates.Single().RondelPosition);
        Assert.Equal(ManeuverPhase.Fleets, game.CurrentManeuverPhase);
    }

    [Fact]
    public async Task DefenderFight_ResumesAndCompletesTheActiveNationsManeuver()
    {
        var gameId = Guid.NewGuid();
        var europePlayerId = Guid.NewGuid();
        var traineeId = Guid.NewGuid();
        var aggressor = new Unit
        {
            Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.Europe, UnitType = UnitType.Fleet,
            TerritoryId = "SouthAtlantic", HasMoved = true, IsHostile = true
        };
        var chinaDefender = new Unit
        {
            Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.China, UnitType = UnitType.Fleet,
            TerritoryId = "SouthAtlantic"
        };

        var game = new Game
        {
            Id = gameId,
            Name = "RL_Training_battle_resume",
            Status = GameStatus.InProgress,
            CurrentTurnNation = Nation.Europe,
            CurrentManeuverPhase = ManeuverPhase.Fleets,
            TurnCount = 77,
            Players = new List<Player>
            {
                new() { Id = europePlayerId, GameId = gameId, IsBot = true, BotName = "RL opponent", BotType = DeterministicRlStrategy.StrategyName },
                new() { Id = traineeId, GameId = gameId, IsBot = true, BotName = "RL-4Agent", BotType = DeterministicRlStrategy.StrategyName }
            },
            NationStates = Enum.GetValues<Nation>().Select(n => new NationState
            {
                GameId = gameId,
                Nation = n,
                ControllerId = n switch
                {
                    Nation.Europe => europePlayerId,
                    Nation.China => traineeId,
                    _ => null
                },
                RondelPosition = n == Nation.Europe ? RondelData.ManeuverSlot1 : null,
                HasMovedThisTurn = n == Nation.Europe
            }).ToList(),
            Units = new List<Unit>
            {
                aggressor,
                chinaDefender,
                new() { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.Europe, UnitType = UnitType.Fleet, TerritoryId = "NorthAtlantic" },
                new() { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.Europe, UnitType = UnitType.Army, TerritoryId = "Paris" }
            },
            PendingBattleTerritoryId = "SouthAtlantic",
            PendingBattleAggressorNation = Nation.Europe,
            PendingBattleAggressorUnitId = aggressor.Id,
            PendingBattleDefenders = new List<Nation> { Nation.China }
        };

        var botService = CreateBotService();
        var server = new TcpTrainingServer(botService, new Mock<ILogger<TcpTrainingServer>>().Object);
        var sessionId = Guid.NewGuid().ToString();
        var sessions = GetSessions();
        sessions[sessionId] = new TcpTrainingServer.TrainingSession { Game = game, RLPlayerId = traineeId };

        var oldTraining = RLBotStrategy.IsTraining;
        RLBotStrategy.IsTraining = true;
        try
        {
            await InvokeStep(server, new TcpTrainingServer.TcpRequest
            {
                Command = "step",
                SessionId = sessionId,
                Action = RLBotStrategy.FightAction
            });
        }
        finally
        {
            RLBotStrategy.TrainingActionOverride.Value = null;
            RLBotStrategy.IsTraining = oldTraining;
            sessions.TryRemove(sessionId, out _);
        }

        Assert.Equal(Nation.China, game.CurrentTurnNation);
        Assert.Equal(78, game.TurnCount);
        Assert.Equal(ManeuverPhase.None, game.CurrentManeuverPhase);
        Assert.Contains(game.Actions, a => a.ActionType == "AutoEndPhase" && a.Nation == Nation.Europe);
        Assert.Contains(game.Actions, a => a.ActionType == "EndTurn" && a.Nation == Nation.Europe);
    }

    private static ConcurrentDictionary<string, TcpTrainingServer.TrainingSession> GetSessions()
    {
        var field = typeof(TcpTrainingServer).GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Static);
        return Assert.IsType<ConcurrentDictionary<string, TcpTrainingServer.TrainingSession>>(field?.GetValue(null));
    }

    private static BotService CreateBotService()
    {
        var hub = new Mock<IHubContext<Imperial2030.Server.Hubs.GameHub>>();
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(new Mock<IClientProxy>().Object);
        hub.Setup(h => h.Clients).Returns(clients.Object);

        return new BotService(
            new Mock<IServiceScopeFactory>().Object,
            hub.Object,
            [new DeterministicRlStrategy(), new DefaultBotStrategy()],
            new Mock<ILogger<BotService>>().Object)
        {
            SkipDelays = true
        };
    }

    private static async Task<TcpTrainingServer.StepResponse?> InvokeStep(
        TcpTrainingServer server, TcpTrainingServer.TcpRequest request)
    {
        var method = typeof(TcpTrainingServer).GetMethod("HandleStepAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        var task = Assert.IsType<Task<TcpTrainingServer.StepResponse?>>(method?.Invoke(server, [request]));
        return await task;
    }

    private sealed class DeterministicRlStrategy : RLBotStrategy
    {
        public const string StrategyName = "DeterministicTrainingRl";

        public DeterministicRlStrategy() : base(StrategyName)
        {
        }

        public override double ScoreManeuverDestination(Game game, Unit unit, string destinationId, Player controller)
            => destinationId == unit.TerritoryId ? 1_000 : -1_000;
    }
}
