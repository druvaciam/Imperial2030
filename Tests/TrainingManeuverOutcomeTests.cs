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

public sealed class TrainingManeuverOutcomeTests
{
    private static Game NewGame(Nation nation)
    {
        var controllerId = Guid.NewGuid();
        return new Game
        {
            Id = Guid.NewGuid(),
            Name = "Holistic maneuver reward",
            Status = GameStatus.InProgress,
            CurrentTurnNation = nation,
            CurrentManeuverPhase = ManeuverPhase.Fleets,
            Players = new List<Player>
            {
                new() { Id = controllerId, IsBot = true, BotType = "RL-4", BotName = "Trainee" }
            },
            NationStates = Enum.GetValues<Nation>().Select(n => new NationState
            {
                Nation = n,
                ControllerId = n == nation ? controllerId : Guid.NewGuid()
            }).ToList(),
            TerritoryStates = new List<TerritoryState>(),
            Units = new List<Unit>(),
            Actions = new List<GameAction>()
        };
    }

    private static Unit Army(Nation nation, string territoryId, bool hostile = false) => new()
    {
        Id = Guid.NewGuid(),
        Nation = nation,
        UnitType = UnitType.Army,
        TerritoryId = territoryId,
        IsHostile = hostile
    };

    private static void SetControl(Game game, Nation nation, params string[] territoryIds)
    {
        foreach (string territoryId in territoryIds)
        {
            game.TerritoryStates.Add(new TerritoryState { TerritoryId = territoryId, Controller = nation });
        }
    }

    [Fact]
    public void VacatingTheLastDefenderOfAThreatenedFactoryLowersPotential()
    {
        var game = NewGame(Nation.USA);
        var defender = Army(Nation.USA, "NewYork");
        game.Units.Add(defender);
        game.Units.Add(Army(Nation.Russia, "Quebec"));
        game.TerritoryStates.Add(new TerritoryState { TerritoryId = "NewYork", HasFactory = true });
        Guid controllerId = game.NationStates.Single(n => n.Nation == Nation.USA).ControllerId!.Value;

        float before = TcpTrainingServer.CalculateManeuverStrategicPotential(game, Nation.USA, controllerId);
        defender.TerritoryId = "Chicago";
        float after = TcpTrainingServer.CalculateManeuverStrategicPotential(game, Nation.USA, controllerId);

        Assert.True(after < before, $"Vacating threatened New York changed potential from {before} to {after}.");
    }

    [Fact]
    public void ReinforcingAThreatenedFactoryRaisesPotential()
    {
        var game = NewGame(Nation.USA);
        var defender = Army(Nation.USA, "Chicago");
        game.Units.Add(defender);
        game.Units.Add(Army(Nation.Russia, "Quebec"));
        game.TerritoryStates.Add(new TerritoryState { TerritoryId = "NewYork", HasFactory = true });
        Guid controllerId = game.NationStates.Single(n => n.Nation == Nation.USA).ControllerId!.Value;

        float before = TcpTrainingServer.CalculateManeuverStrategicPotential(game, Nation.USA, controllerId);
        defender.TerritoryId = "NewYork";
        float after = TcpTrainingServer.CalculateManeuverStrategicPotential(game, Nation.USA, controllerId);

        Assert.True(after > before, $"Reinforcing threatened New York changed potential from {before} to {after}.");
    }

    [Theory]
    [InlineData(Nation.Brazil, "Brasilia", "Colombia", "Peru", "Argentina")]
    [InlineData(Nation.Europe, "Berlin", "Turkey", "Ukraine", "NearEast")]
    public void ASecondArmyAtAUsefulForwardStagingPointIsNotTreatedAsRedundant(
        Nation nation,
        string origin,
        string staging,
        string controlledOne,
        string controlledTwo)
    {
        var game = NewGame(nation);
        var mover = Army(nation, origin);
        game.Units.Add(mover);
        game.Units.Add(Army(nation, staging));
        SetControl(game, nation, staging, controlledOne, controlledTwo);
        Guid controllerId = game.NationStates.Single(n => n.Nation == nation).ControllerId!.Value;

        float before = TcpTrainingServer.CalculateManeuverStrategicPotential(game, nation, controllerId);
        mover.TerritoryId = staging;
        float after = TcpTrainingServer.CalculateManeuverStrategicPotential(game, nation, controllerId);

        Assert.True(after > before,
            $"A second {nation} army staged in {staging} changed potential from {before} to {after}.");
    }

    [Fact]
    public void ForwardStagingValueIsCappedAtTheFactoryDestructionForceSize()
    {
        var game = NewGame(Nation.Brazil);
        SetControl(game, Nation.Brazil, "Colombia", "Peru", "Argentina");
        for (int index = 0; index < ManeuverRules.DestroyFactoryArmyCost; index++)
        {
            game.Units.Add(Army(Nation.Brazil, "Colombia"));
        }
        Guid controllerId = game.NationStates.Single(n => n.Nation == Nation.Brazil).ControllerId!.Value;

        float atCap = TcpTrainingServer.CalculateManeuverStrategicPotential(game, Nation.Brazil, controllerId);
        game.Units.Add(Army(Nation.Brazil, "Colombia"));
        float aboveCap = TcpTrainingServer.CalculateManeuverStrategicPotential(game, Nation.Brazil, controllerId);

        Assert.Equal(atCap, aboveCap);
    }

    [Fact]
    public void APositionNeutralMoveWithNoDirectEventGetsOneWastedManeuverPenalty()
    {
        var snapshot = new TcpTrainingServer.ManeuverOutcomeSnapshot
        {
            Nation = Nation.India,
            TurnCount = 10,
            InitialStrategicPotential = 4f,
            AnyUnitChangedTerritory = true
        };

        float reward = TcpTrainingServer.CalculateManeuverOutcomeReward(snapshot, 4f);

        Assert.Equal(-TcpTrainingServer.WastedManeuverResultPenalty, reward);
    }

    [Fact]
    public void AChangedPositionThatDoesNotImproveGetsAtLeastTheSingleWastedResultPenalty()
    {
        var snapshot = new TcpTrainingServer.ManeuverOutcomeSnapshot
        {
            Nation = Nation.India,
            TurnCount = 10,
            InitialStrategicPotential = 4f,
            AnyUnitChangedTerritory = true
        };

        float reward = TcpTrainingServer.CalculateManeuverOutcomeReward(snapshot, 3.75f);

        Assert.Equal(-TcpTrainingServer.WastedManeuverResultPenalty, reward);
    }

    [Fact]
    public void IranToAfghanistanWithNoChangedSafetyReachOrEventIsOneWastedManeuver()
    {
        var game = NewGame(Nation.India);
        Guid controllerId = game.NationStates.Single(n => n.Nation == Nation.India).ControllerId!.Value;
        foreach (var nationState in game.NationStates) nationState.ControllerId = controllerId;
        SetControl(game, Nation.India, "Iran", "Turkey", "Afghanistan", "NearEast", "Kazakhstan");
        var mover = Army(Nation.India, "Iran");
        game.Units.Add(mover);

        float before = TcpTrainingServer.CalculateManeuverStrategicPotential(game, Nation.India, controllerId);
        mover.TerritoryId = "Afghanistan";
        float after = TcpTrainingServer.CalculateManeuverStrategicPotential(game, Nation.India, controllerId);
        var snapshot = new TcpTrainingServer.ManeuverOutcomeSnapshot
        {
            Nation = Nation.India,
            TurnCount = 10,
            InitialStrategicPotential = before,
            AnyUnitChangedTerritory = true
        };

        Assert.Equal(before, after);
        Assert.Equal(-TcpTrainingServer.WastedManeuverResultPenalty,
            TcpTrainingServer.CalculateManeuverOutcomeReward(snapshot, after));
    }

    [Fact]
    public void ADirectFlagCombatHomeOrFactoryEventPreventsTheWastedResultPenalty()
    {
        var snapshot = new TcpTrainingServer.ManeuverOutcomeSnapshot
        {
            Nation = Nation.India,
            TurnCount = 10,
            InitialStrategicPotential = 4f,
            AnyUnitChangedTerritory = true,
            DirectEventOccurred = true
        };

        Assert.Equal(0f, TcpTrainingServer.CalculateManeuverOutcomeReward(snapshot, 4f));
    }

    [Fact]
    public void PassingWithoutDamagingASoundPositionIsNeutral()
    {
        var snapshot = new TcpTrainingServer.ManeuverOutcomeSnapshot
        {
            Nation = Nation.India,
            TurnCount = 10,
            InitialStrategicPotential = 4f
        };

        Assert.Equal(0f, TcpTrainingServer.CalculateManeuverOutcomeReward(snapshot, 4f));
    }

    [Fact]
    public void HolisticRewardIsSymmetricallyBounded()
    {
        var snapshot = new TcpTrainingServer.ManeuverOutcomeSnapshot
        {
            Nation = Nation.India,
            TurnCount = 10,
            InitialStrategicPotential = 0f
        };

        Assert.Equal(TcpTrainingServer.MaxManeuverOutcomeReward,
            TcpTrainingServer.CalculateManeuverOutcomeReward(snapshot, 1_000f));

        snapshot.InitialStrategicPotential = 1_000f;
        Assert.Equal(-TcpTrainingServer.MaxManeuverOutcomeReward,
            TcpTrainingServer.CalculateManeuverOutcomeReward(snapshot, 0f));
    }

    [Theory]
    [InlineData(RondelData.ManeuverSlot1)]
    [InlineData(RondelData.ManeuverSlot2)]
    public void EitherManeuverSlotArmsExactlyOneSnapshot(int targetSlot)
    {
        var game = NewGame(Nation.Brazil);
        var nationState = game.NationStates.Single(n => n.Nation == Nation.Brazil);
        var session = new TcpTrainingServer.TrainingSession
        {
            Game = game,
            RLPlayerId = nationState.ControllerId!.Value
        };

        Assert.True(TcpTrainingServer.TryArmManeuverOutcome(session, game, Nation.Brazil, targetSlot));
        var first = session.ManeuverOutcome;
        Assert.NotNull(first);
        Assert.False(TcpTrainingServer.TryArmManeuverOutcome(session, game, Nation.Brazil, targetSlot));
        Assert.Same(first, session.ManeuverOutcome);
    }

    [Fact]
    public void BattleAndFactoryDecisionsDelayCompletionUntilTheWholeManeuverIsResolved()
    {
        var game = NewGame(Nation.Brazil);
        var nationState = game.NationStates.Single(n => n.Nation == Nation.Brazil);
        var session = new TcpTrainingServer.TrainingSession
        {
            Game = game,
            RLPlayerId = nationState.ControllerId!.Value
        };
        TcpTrainingServer.TryArmManeuverOutcome(session, game, Nation.Brazil, RondelData.ManeuverSlot1);
        game.CurrentManeuverPhase = ManeuverPhase.None;

        game.PendingBattleDefenders.Add(Nation.USA);
        Assert.False(TcpTrainingServer.IsManeuverOutcomeReady(session, game));

        game.PendingBattleDefenders.Clear();
        session.PendingFactoryDestructionTerritoryId = "NewYork";
        Assert.False(TcpTrainingServer.IsManeuverOutcomeReady(session, game));

        session.PendingFactoryDestructionTerritoryId = null;
        game.IsInvestorTurn = true;
        Assert.False(TcpTrainingServer.IsManeuverOutcomeReady(session, game));

        game.IsInvestorTurn = false;
        Assert.True(TcpTrainingServer.IsManeuverOutcomeReady(session, game));
    }

    [Fact]
    public void CompletingTheOutcomeClearsTheSessionSnapshot()
    {
        var game = NewGame(Nation.Brazil);
        var nationState = game.NationStates.Single(n => n.Nation == Nation.Brazil);
        var session = new TcpTrainingServer.TrainingSession
        {
            Game = game,
            RLPlayerId = nationState.ControllerId!.Value
        };
        TcpTrainingServer.TryArmManeuverOutcome(session, game, Nation.Brazil, RondelData.ManeuverSlot1);

        float reward = TcpTrainingServer.CompleteManeuverOutcome(
            session,
            session.ManeuverOutcome!.InitialStrategicPotential);

        Assert.Equal(0f, reward);
        Assert.Null(session.ManeuverOutcome);
    }

    [Fact]
    public void ExistingDirectEventRewardsKeepTheirValues()
    {
        Assert.Equal(1f, TcpTrainingServer.FlagPlacementReward);
        Assert.Equal(1f, TcpTrainingServer.EnemyUnitDestroyedReward);
        Assert.Equal(15f, TcpTrainingServer.HomeReliefReward);
        Assert.Equal(3f, TcpTrainingServer.EnemyFactoryDestroyedReward);
        Assert.Equal(2f, TcpTrainingServer.EnemyFactoryOccupiedReward);
    }

    [Fact]
    public async Task TrainingStepArmsAndCompletesOneSnapshotAcrossTheActualManeuverFlow()
    {
        var game = NewGame(Nation.Brazil);
        var rlPlayer = game.Players.Single();
        var scoreOnlyOpponent = new Player
        {
            Id = Guid.NewGuid(), IsBot = false, UserId = "score-opponent", Cash = 0
        };
        game.Players.Add(scoreOnlyOpponent);
        game.Name = "RL_Training_HolisticManeuver";
        game.CurrentManeuverPhase = ManeuverPhase.None;
        game.TurnCount = 5;
        game.Bonds = new List<Bond>();
        foreach (var nationState in game.NationStates) nationState.ControllerId = rlPlayer.Id;
        var brazil = game.NationStates.Single(state => state.Nation == Nation.Brazil);
        brazil.RondelPosition = RondelData.ProductionSlot1;
        game.Units.Add(Army(Nation.Brazil, "Brasilia"));

        var server = CreateTrainingServer();
        var sessionId = Guid.NewGuid().ToString();
        var sessions = GetSessions();
        var session = new TcpTrainingServer.TrainingSession { Game = game, RLPlayerId = rlPlayer.Id };
        sessions[sessionId] = session;

        try
        {
            var rondelResponse = await InvokeStep(server, sessionId, action: 0);

            Assert.NotNull(rondelResponse);
            Assert.NotNull(session.ManeuverOutcome);
            Assert.Equal(Nation.Brazil, session.ManeuverOutcome.Nation);
            Assert.Equal(ManeuverPhase.Armies, game.CurrentManeuverPhase);
            Assert.Equal("Brasilia", session.ManeuverSelectedTerritoryId);

            int peruIndex = Array.IndexOf(RLBotStrategy.AllManeuverTerritories, "Peru");
            Assert.True(peruIndex >= 0);
            var moveResponse = await InvokeStep(server, sessionId, action: 127 + peruIndex);

            Assert.NotNull(moveResponse);
            Assert.Null(session.ManeuverOutcome);
            Assert.Equal(6, game.TurnCount);
            Assert.Equal(Nation.USA, game.CurrentTurnNation);
            Assert.Equal("Peru", game.Units.Single(unit => unit.Nation == Nation.Brazil).TerritoryId);
            Assert.Equal(Nation.Brazil,
                game.TerritoryStates.Single(state => state.TerritoryId == "Peru").Controller);
            Assert.True(moveResponse.Reward >= TcpTrainingServer.FlagPlacementReward);
        }
        finally
        {
            RLBotStrategy.TrainingActionOverride.Value = null;
            sessions.TryRemove(sessionId, out _);
        }
    }

    private static TcpTrainingServer CreateTrainingServer()
    {
        var hub = new Mock<IHubContext<Imperial2030.Server.Hubs.GameHub>>();
        var clients = new Mock<IHubClients>();
        clients.Setup(client => client.Group(It.IsAny<string>())).Returns(new Mock<IClientProxy>().Object);
        hub.Setup(context => context.Clients).Returns(clients.Object);

        var botService = new BotService(
            new Mock<IServiceScopeFactory>().Object,
            hub.Object,
            new List<Imperial2030.Server.Services.Bots.IBotStrategy> { new DefaultBotStrategy() },
            new Mock<ILogger<BotService>>().Object)
        {
            SkipDelays = true
        };

        return new TcpTrainingServer(botService, new Mock<ILogger<TcpTrainingServer>>().Object);
    }

    private static ConcurrentDictionary<string, TcpTrainingServer.TrainingSession> GetSessions()
    {
        var field = typeof(TcpTrainingServer).GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Static);
        return Assert.IsType<ConcurrentDictionary<string, TcpTrainingServer.TrainingSession>>(field?.GetValue(null));
    }

    private static async Task<TcpTrainingServer.StepResponse?> InvokeStep(
        TcpTrainingServer server,
        string sessionId,
        int action)
    {
        var method = typeof(TcpTrainingServer).GetMethod("HandleStepAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        var request = new TcpTrainingServer.TcpRequest
        {
            Command = "step",
            SessionId = sessionId,
            Action = action
        };
        var task = Assert.IsType<Task<TcpTrainingServer.StepResponse?>>(method?.Invoke(server, [request]));
        return await task;
    }
}
