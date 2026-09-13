using System.Diagnostics;
using System.Text.Json;
using Imperial2030.Server.Data;
using Imperial2030.Server.Helpers;
using Imperial2030.Server.Hubs;
using Imperial2030.Server.Models;
using Imperial2030.Server.Services;
using Imperial2030.Server.Services.Bots.Strategies;
using Imperial2030.Shared.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace Imperial2030.Tests;

/// <summary>
/// Compares RL model generations on paired, randomly-generated starting scenarios.
///
/// Within one scenario RL-2, RL-3 and RL-4 receive the same player seating, starting bond-package
/// distribution, RL-controlled nation, Investor-card holder and five opponents assigned to the same
/// nations. The next scenario is generated afresh, so the comparison covers different starts without
/// giving one model an easier random deal than another.
///
/// This is a measurement, not a quality gate: stochastic policies and opponents make rank/win thresholds
/// inherently noisy. Assertions only guard that the games were genuinely matched and completed.
/// </summary>
public class RLVersionComparisonTests
{
    private const int ScenarioCount = 12;
    private static readonly TimeSpan HardTimeout = TimeSpan.FromMinutes(8);

    private static readonly string[] ModelTypes = ["RL-2", "RL-3", "RL-4"];
    private static readonly string[] OpponentTypes = ["Random", "Default", "Greedy", "Aggressive", "Friendly"];

    private readonly ITestOutputHelper _output;

    public RLVersionComparisonTests(ITestOutputHelper output) => _output = output;

    private sealed record MatchedScenario(
        int Number,
        Nation RlNation,
        IReadOnlyList<Guid> PlayerIds,
        IReadOnlyDictionary<Nation, Guid> Distribution,
        IReadOnlyDictionary<Guid, string> OpponentByPlayer);

    private sealed record GameResult(
        string Model,
        bool Won,
        int Rank,
        int Score,
        int RelativeMargin,
        int Turns,
        string InitialStateFingerprint);

    private sealed record HeadToHeadResult(
        int Scenario,
        int Rotation,
        Guid PlayerId,
        string Model,
        bool Won,
        int Rank,
        int Score,
        int RelativeMargin,
        int Turns,
        string InitialStateFingerprint);

    [Fact]
    public async Task CompareRLVersionsOnMatchedRandomStarts()
    {
        var stopwatch = Stopwatch.StartNew();
        var allResults = new List<GameResult>();

        for (int scenarioNumber = 1; scenarioNumber <= ScenarioCount; scenarioNumber++)
        {
            Assert.True(stopwatch.Elapsed < HardTimeout,
                $"Comparison exceeded the {HardTimeout.TotalMinutes}-minute hard timeout before scenario {scenarioNumber}.");

            var scenario = CreateRandomScenario(scenarioNumber);
            var scenarioResults = new List<GameResult>();

            foreach (var model in ModelTypes)
            {
                scenarioResults.Add(await PlayScenario(model, scenario, stopwatch));
            }

            // This is the central comparability guarantee. The fingerprint deliberately excludes bot
            // names/types, but includes every setup value that can affect play.
            Assert.Single(scenarioResults.Select(r => r.InitialStateFingerprint).Distinct());

            allResults.AddRange(scenarioResults);
            _output.WriteLine(
                $"Scenario {scenario.Number,2}: RL nation={scenario.RlNation,-6}; " +
                string.Join(" | ", scenarioResults.Select(r =>
                    $"{r.Model}: rank {r.Rank}, score {r.Score}, margin {r.RelativeMargin:+#;-#;0}")));
        }

        _output.WriteLine("");
        _output.WriteLine($"=== Matched RL comparison: {ScenarioCount} randomized scenarios ===");
        _output.WriteLine($"{"model",-6} {"wins",8} {"avg rank",9} {"avg score",10} {"avg margin",11} {"avg turns",10}");

        foreach (var model in ModelTypes)
        {
            var results = allResults.Where(r => r.Model == model).ToList();
            _output.WriteLine(
                $"{model,-6} {results.Count(r => r.Won),2}/{results.Count,-5} " +
                $"{results.Average(r => r.Rank),9:0.00} " +
                $"{results.Average(r => r.Score),10:0.00} " +
                $"{results.Average(r => r.RelativeMargin),11:+0.00;-0.00;0.00} " +
                $"{results.Average(r => r.Turns),10:0.0}");
        }

        Assert.Equal(ScenarioCount * ModelTypes.Length, allResults.Count);
        Assert.All(ModelTypes, model =>
            Assert.Equal(ScenarioCount, allResults.Count(r => r.Model == model)));
    }

    [Fact]
    public async Task CompareRLVersionsHeadToHeadOnRotatedRandomStarts()
    {
        var stopwatch = Stopwatch.StartNew();
        var allResults = new List<HeadToHeadResult>();

        for (int scenarioNumber = 1; scenarioNumber <= ScenarioCount; scenarioNumber++)
        {
            Assert.True(stopwatch.Elapsed < HardTimeout,
                $"Head-to-head comparison exceeded the {HardTimeout.TotalMinutes}-minute hard timeout before scenario {scenarioNumber}.");

            var scenario = CreateHeadToHeadScenario(scenarioNumber);
            var scenarioResults = new List<HeadToHeadResult>();

            // Rotate the models through all three seats. Every game still contains RL-2, RL-3 and RL-4,
            // while every model receives every one of this scenario's nation/bond packages exactly once.
            for (int rotation = 0; rotation < ModelTypes.Length; rotation++)
            {
                var modelByPlayer = scenario.PlayerIds
                    .Select((playerId, seat) => (playerId, model: ModelTypes[(seat + rotation) % ModelTypes.Length]))
                    .ToDictionary(pair => pair.playerId, pair => pair.model);

                var rotationResults = await PlayHeadToHeadScenario(
                    scenario, rotation + 1, modelByPlayer, stopwatch);

                Assert.Equal(ModelTypes.OrderBy(x => x), rotationResults.Select(r => r.Model).OrderBy(x => x));
                scenarioResults.AddRange(rotationResults);

                _output.WriteLine(
                    $"Scenario {scenario.Number,2}, rotation {rotation + 1}: " +
                    string.Join(" | ", rotationResults.OrderBy(r => r.Model).Select(r =>
                        $"{r.Model}: rank {r.Rank}, score {r.Score}, margin {r.RelativeMargin:+#;-#;0}")));
            }

            // Bot identity is deliberately excluded from the setup fingerprint. All three rotations must
            // therefore begin from the exact same board, seating, bonds and Investor-card holder.
            Assert.Single(scenarioResults.Select(r => r.InitialStateFingerprint).Distinct());

            // Seat rotation is the fairness guarantee: within a scenario each model plays from each player
            // seat once, so no version is permanently tied to one pair of nations or one Investor position.
            foreach (var playerId in scenario.PlayerIds)
            {
                Assert.Equal(
                    ModelTypes.OrderBy(x => x),
                    scenarioResults.Where(r => r.PlayerId == playerId).Select(r => r.Model).OrderBy(x => x));
            }

            allResults.AddRange(scenarioResults);
        }

        _output.WriteLine("");
        _output.WriteLine($"=== RL head-to-head comparison: {ScenarioCount} randomized scenarios, three seat rotations each ===");
        _output.WriteLine($"{"model",-6} {"wins",8} {"avg rank",9} {"avg score",10} {"avg margin",11} {"avg turns",10}");

        foreach (var model in ModelTypes)
        {
            var results = allResults.Where(r => r.Model == model).ToList();
            _output.WriteLine(
                $"{model,-6} {results.Count(r => r.Won),2}/{results.Count,-5} " +
                $"{results.Average(r => r.Rank),9:0.00} " +
                $"{results.Average(r => r.Score),10:0.00} " +
                $"{results.Average(r => r.RelativeMargin),11:+0.00;-0.00;0.00} " +
                $"{results.Average(r => r.Turns),10:0.0}");
        }

        int expectedResultsPerModel = ScenarioCount * ModelTypes.Length;
        Assert.Equal(expectedResultsPerModel * ModelTypes.Length, allResults.Count);
        Assert.All(ModelTypes, model =>
            Assert.Equal(expectedResultsPerModel, allResults.Count(r => r.Model == model)));
    }

    private static MatchedScenario CreateRandomScenario(int number)
    {
        var playerIds = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).OrderBy(id => id).ToList();
        var shuffledPlayers = Shuffle(playerIds);
        var nations = Enum.GetValues<Nation>();
        var distribution = nations
            .Select((nation, index) => (nation, playerId: shuffledPlayers[index]))
            .ToDictionary(pair => pair.nation, pair => pair.playerId);

        var rlNation = nations[Random.Shared.Next(nations.Length)];
        var rlPlayerId = distribution[rlNation];
        var shuffledOpponents = Shuffle(OpponentTypes);
        var opponentByPlayer = nations
            .Where(n => n != rlNation)
            .Select((nation, index) => (playerId: distribution[nation], botType: shuffledOpponents[index]))
            .ToDictionary(pair => pair.playerId, pair => pair.botType);

        Assert.DoesNotContain(rlPlayerId, opponentByPlayer.Keys);
        Assert.Equal(OpponentTypes.OrderBy(x => x), opponentByPlayer.Values.OrderBy(x => x));

        return new MatchedScenario(number, rlNation, playerIds, distribution, opponentByPlayer);
    }

    private static MatchedScenario CreateHeadToHeadScenario(int number)
    {
        var playerIds = Enumerable.Range(0, ModelTypes.Length)
            .Select(_ => Guid.NewGuid())
            .OrderBy(id => id)
            .ToList();
        var shuffledPlayers = Shuffle(playerIds);

        // These are the official three-player package pairs already used by GameSetupHelper. Randomly
        // assigning the three players to the pairs gives every scenario a fresh initial distribution.
        var distribution = new Dictionary<Nation, Guid>
        {
            [Nation.India] = shuffledPlayers[0],
            [Nation.USA] = shuffledPlayers[0],
            [Nation.Russia] = shuffledPlayers[1],
            [Nation.Brazil] = shuffledPlayers[1],
            [Nation.China] = shuffledPlayers[2],
            [Nation.Europe] = shuffledPlayers[2]
        };

        return new MatchedScenario(
            number,
            Nation.Russia,
            playerIds,
            distribution,
            new Dictionary<Guid, string>());
    }

    private async Task<GameResult> PlayScenario(string model, MatchedScenario scenario, Stopwatch overallStopwatch)
    {
        string databaseName = $"RLComparison_{scenario.Number}_{model}_{Guid.NewGuid():N}";
        await using var context = CreateContext(databaseName);
        var gameId = Guid.NewGuid();

        context.Games.Add(new Game
        {
            Id = gameId,
            Name = $"Matched scenario {scenario.Number} ({model})",
            MaxPlayers = 6,
            Status = GameStatus.Lobby,
            CurrentTurnNation = Nation.Russia
        });
        context.Players.AddRange(scenario.PlayerIds.Select((id, index) => new Player
        {
            Id = id,
            GameId = gameId,
            UserId = $"comparison-{id:N}",
            IsHost = index == 0,
            IsBot = false,
            BotName = $"Seat {index}"
        }));
        await context.SaveChangesAsync();

        await GameSetupHelper.InitializeGameAsync(
            context,
            gameId,
            scenario.Distribution.ToDictionary(pair => pair.Key, pair => pair.Value));

        var rlPlayerId = scenario.Distribution[scenario.RlNation];
        var players = await context.Players.Where(p => p.GameId == gameId).ToListAsync();
        foreach (var player in players)
        {
            player.IsBot = true;
            player.BotType = player.Id == rlPlayerId ? model : scenario.OpponentByPlayer[player.Id];
            player.BotName = player.Id == rlPlayerId
                ? $"{model} Bot"
                : $"{player.BotType} Bot";
        }
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var startingGame = await context.Games
            .Include(g => g.Players)
            .Include(g => g.NationStates)
            .Include(g => g.Bonds)
            .Include(g => g.TerritoryStates)
            .AsSplitQuery()
            .SingleAsync(g => g.Id == gameId);

        Assert.Equal(rlPlayerId,
            startingGame.NationStates.Single(n => n.Nation == scenario.RlNation).ControllerId);
        Assert.Equal(OpponentTypes.OrderBy(x => x),
            startingGame.Players.Where(p => p.Id != rlPlayerId).Select(p => p.BotType!).OrderBy(x => x));

        string initialFingerprint = Fingerprint(startingGame);
        var botService = CreateBotService(databaseName, model);
        botService.SkipDelays = true;
        botService.TriggerBotTurn(gameId, 0);

        while (overallStopwatch.Elapsed < HardTimeout)
        {
            await Task.Delay(20);
            await using var snapshotContext = CreateContext(databaseName);
            var status = await snapshotContext.Games.AsNoTracking()
                .Where(g => g.Id == gameId)
                .Select(g => g.Status)
                .SingleAsync();
            if (status == GameStatus.Finished) break;
        }

        Assert.True(overallStopwatch.Elapsed < HardTimeout,
            $"{model} did not finish matched scenario {scenario.Number} within the hard timeout.");

        await using var finalContext = CreateContext(databaseName);
        var finalGame = await finalContext.Games.AsNoTracking()
            .Include(g => g.Players)
            .Include(g => g.NationStates)
            .Include(g => g.Bonds)
            .AsSplitQuery()
            .SingleAsync(g => g.Id == gameId);
        Assert.Equal(GameStatus.Finished, finalGame.Status);

        var ranked = finalGame.GetRankedPlayers();
        int rank = ranked.FindIndex(p => p.Id == rlPlayerId) + 1;
        int score = finalGame.CalculateScore(rlPlayerId);
        int bestOpponentScore = finalGame.Players
            .Where(p => p.Id != rlPlayerId)
            .Max(p => finalGame.CalculateScore(p.Id));

        return new GameResult(
            model,
            rank == 1,
            rank,
            score,
            score - bestOpponentScore,
            finalGame.TurnCount,
            initialFingerprint);
    }

    private async Task<List<HeadToHeadResult>> PlayHeadToHeadScenario(
        MatchedScenario scenario,
        int rotation,
        IReadOnlyDictionary<Guid, string> modelByPlayer,
        Stopwatch overallStopwatch)
    {
        string databaseName = $"RLHeadToHead_{scenario.Number}_{rotation}_{Guid.NewGuid():N}";
        await using var context = CreateContext(databaseName);
        var gameId = Guid.NewGuid();

        context.Games.Add(new Game
        {
            Id = gameId,
            Name = $"RL head-to-head scenario {scenario.Number}, rotation {rotation}",
            MaxPlayers = ModelTypes.Length,
            Status = GameStatus.Lobby,
            CurrentTurnNation = Nation.Russia
        });
        context.Players.AddRange(scenario.PlayerIds.Select((id, index) => new Player
        {
            Id = id,
            GameId = gameId,
            UserId = $"head-to-head-{id:N}",
            IsHost = index == 0,
            IsBot = false,
            BotName = $"Seat {index}"
        }));
        await context.SaveChangesAsync();

        await GameSetupHelper.InitializeGameAsync(
            context,
            gameId,
            scenario.Distribution.ToDictionary(pair => pair.Key, pair => pair.Value));

        var players = await context.Players.Where(p => p.GameId == gameId).ToListAsync();
        foreach (var player in players)
        {
            player.IsBot = true;
            player.BotType = modelByPlayer[player.Id];
            player.BotName = $"{player.BotType} Bot";
        }
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var startingGame = await context.Games
            .Include(g => g.Players)
            .Include(g => g.NationStates)
            .Include(g => g.Bonds)
            .Include(g => g.TerritoryStates)
            .AsSplitQuery()
            .SingleAsync(g => g.Id == gameId);

        Assert.Equal(ModelTypes.OrderBy(x => x),
            startingGame.Players.Select(p => p.BotType!).OrderBy(x => x));
        Assert.All(startingGame.Players, player =>
            Assert.Equal(2, startingGame.NationStates.Count(n => n.ControllerId == player.Id)));

        string initialFingerprint = Fingerprint(startingGame);
        var botService = CreateBotService(databaseName, ModelTypes);
        botService.SkipDelays = true;
        botService.TriggerBotTurn(gameId, 0);

        while (overallStopwatch.Elapsed < HardTimeout)
        {
            await Task.Delay(20);
            await using var snapshotContext = CreateContext(databaseName);
            var status = await snapshotContext.Games.AsNoTracking()
                .Where(g => g.Id == gameId)
                .Select(g => g.Status)
                .SingleAsync();
            if (status == GameStatus.Finished) break;
        }

        Assert.True(overallStopwatch.Elapsed < HardTimeout,
            $"Head-to-head rotation {rotation} did not finish scenario {scenario.Number} within the hard timeout.");

        await using var finalContext = CreateContext(databaseName);
        var finalGame = await finalContext.Games.AsNoTracking()
            .Include(g => g.Players)
            .Include(g => g.NationStates)
            .Include(g => g.Bonds)
            .AsSplitQuery()
            .SingleAsync(g => g.Id == gameId);
        Assert.Equal(GameStatus.Finished, finalGame.Status);

        var ranked = finalGame.GetRankedPlayers();
        return finalGame.Players.Select(player =>
        {
            int score = finalGame.CalculateScore(player.Id);
            int bestOpponentScore = finalGame.Players
                .Where(opponent => opponent.Id != player.Id)
                .Max(opponent => finalGame.CalculateScore(opponent.Id));
            int rank = ranked.FindIndex(rankedPlayer => rankedPlayer.Id == player.Id) + 1;

            return new HeadToHeadResult(
                scenario.Number,
                rotation,
                player.Id,
                player.BotType!,
                rank == 1,
                rank,
                score,
                score - bestOpponentScore,
                finalGame.TurnCount,
                initialFingerprint);
        }).ToList();
    }

    private static ApplicationDbContext CreateContext(string databaseName) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options);

    private static BotService CreateBotService(string databaseName, params string[] models)
    {
        var hub = new Mock<IHubContext<GameHub>>();
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(new Mock<IClientProxy>().Object);
        clients.Setup(c => c.All).Returns(new Mock<IClientProxy>().Object);
        hub.Setup(h => h.Clients).Returns(clients.Object);

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(factory => factory.CreateScope()).Returns(() =>
        {
            var scope = new Mock<IServiceScope>();
            var provider = new Mock<IServiceProvider>();
            provider.Setup(p => p.GetService(typeof(ApplicationDbContext)))
                .Returns(() => CreateContext(databaseName));
            provider.Setup(p => p.GetService(typeof(INotificationService)))
                .Returns(new Mock<INotificationService>().Object);
            scope.Setup(s => s.ServiceProvider).Returns(provider.Object);
            return scope.Object;
        });

        var strategies = new List<Imperial2030.Server.Services.Bots.IBotStrategy>
        {
            new RandomBotStrategy(),
            new DefaultBotStrategy(),
            new GreedyBotStrategy(),
            new AggressiveBotStrategy(),
            new FriendlyBotStrategy()
        };
        strategies.AddRange(models.Distinct().Select(model => new RLBotStrategy(model)));

        return new BotService(
            scopeFactory.Object,
            hub.Object,
            strategies,
            NullLogger<BotService>.Instance);
    }

    private static string Fingerprint(Game game) => JsonSerializer.Serialize(new
    {
        game.CurrentTurnNation,
        game.InvestorCardHolderId,
        Players = game.Players.OrderBy(p => p.Id).Select(p => new { p.Id, p.Cash }),
        Nations = game.NationStates.OrderBy(n => n.Nation).Select(n => new
        {
            n.Nation,
            n.ControllerId,
            n.Treasury,
            n.Power,
            n.RondelPosition
        }),
        Bonds = game.Bonds.OrderBy(b => b.Nation).ThenBy(b => b.Cost)
            .Select(b => new { b.Nation, b.Cost, b.Interest, b.HolderId }),
        Territories = game.TerritoryStates.OrderBy(t => t.TerritoryId)
            .Select(t => new { t.TerritoryId, t.HasFactory, t.Controller })
    });

    private static List<T> Shuffle<T>(IEnumerable<T> values)
    {
        var result = values.ToList();
        for (int i = result.Count - 1; i > 0; i--)
        {
            int swapIndex = Random.Shared.Next(i + 1);
            (result[i], result[swapIndex]) = (result[swapIndex], result[i]);
        }
        return result;
    }
}
