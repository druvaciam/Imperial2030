using System.Reflection;
using Imperial2030.Server.Models;
using Imperial2030.Server.Services;
using Imperial2030.Server.Services.Bots.Strategies;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Imperial2030.Tests;

/// <summary>
/// The training server and the deployed ONNX strategy must encode an identical observation for an
/// identical game state. A model trained against features that are absent or different at inference
/// time is being deployed into a different environment from the one it learned.
/// </summary>
public class RLStateVectorParityTests
{
    [Fact]
    public void TrainingAndInferenceEncodeTheSameState()
    {
        var rlPlayer = new Player
        {
            Id = Guid.NewGuid(),
            IsBot = true,
            BotType = "RL-4",
            BotName = "RL-4Agent",
            Cash = 13
        };
        var opponent = new Player
        {
            Id = Guid.NewGuid(),
            IsBot = true,
            BotType = "Default",
            BotName = "Default Bot",
            Cash = 13
        };

        var game = new Game
        {
            Name = "RL state-vector parity",
            Status = GameStatus.InProgress,
            CurrentTurnNation = Nation.Russia,
            Players = new List<Player> { rlPlayer, opponent },
            Bonds = new List<Bond>(),
            NationStates = Enum.GetValues<Nation>()
                .Select(n => new NationState
                {
                    Nation = n,
                    ControllerId = n == Nation.Russia ? rlPlayer.Id : opponent.Id,
                    Treasury = 0,
                    Power = 0,
                    // From slot 7, actions 0..5 preview Taxation, Factory, Production,
                    // Maneuver, Investor and Import respectively. With this empty board all six
                    // choices are wasted, exercising every penalty-preview branch.
                    RondelPosition = RondelData.ManeuverSlot2
                })
                .ToList(),
            TerritoryStates = new List<TerritoryState>(),
            Units = new List<Unit>(),
            Actions = new List<GameAction>()
        };

        var inferenceStrategy = new RLBotStrategy("StateVectorParity-NoModel");
        var inferenceMethod = typeof(RLBotStrategy).GetMethod(
            "GetStateVector", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(inferenceMethod);
        var inferenceState = Assert.IsType<float[]>(inferenceMethod!.Invoke(
            inferenceStrategy, new object?[] { game, rlPlayer, null }));

        var trainingServer = new TcpTrainingServer(
            null!, NullLogger<TcpTrainingServer>.Instance);
        var trainingMethod = typeof(TcpTrainingServer).GetMethod(
            "GetStateVector", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(trainingMethod);
        var trainingState = Assert.IsType<float[]>(trainingMethod!.Invoke(
            trainingServer, new object?[] { game, rlPlayer.Id, null }));

        Assert.Equal(RLBotStrategy.StateSize, inferenceState.Length);
        Assert.Equal(RLBotStrategy.StateSize, trainingState.Length);
        Assert.Equal(trainingState, inferenceState);
    }
}
