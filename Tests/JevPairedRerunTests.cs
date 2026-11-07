using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Imperial2030.Server.Data;
using Imperial2030.Server.Helpers;
using Imperial2030.Server.Hubs;
using Imperial2030.Server.Models;
using Imperial2030.Server.Services;
using Imperial2030.Server.Services.Bots;
using Imperial2030.Server.Services.Bots.Strategies;
using Imperial2030.Server.Services.TypeSafe;
using Imperial2030.Shared.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace Imperial2030.Tests;

/// <summary>
/// The same game, played twice: once as the bot plays it, once with the layer switched on. Same deal,
/// same seats, same opponents, and - through <see cref="BotRandom"/> - the same stream of random numbers,
/// so the two runs are identical until the first decision the layer changes. Everything after that is
/// the layer's doing.
///
/// Why bother: comparing two bots in separate games means fighting a 40-point swing per game, which takes
/// hundreds of games to see through. Here the deal is held fixed and the swing is the answer, so the
/// same question needs a tenth of the games - and of the API calls.
///
/// <see cref="TheSameSeedPlaysTheSameGame"/> runs without a key and guards the whole idea: if a seeded
/// rerun ever stops reproducing, the comparison below is measuring noise again.
/// </summary>
[Collection(RlTrainingFlagCollection.Name)]
public class JevPairedRerunTests
{
    private readonly ITestOutputHelper _output;
    public JevPairedRerunTests(ITestOutputHelper output) => _output = output;

    private const string ApiKeyEnvironmentVariable = "TypeSafe__ApiKey";
    private static string BaseBot => Environment.GetEnvironmentVariable("JEV_BASE") is { Length: > 0 } chosen ? chosen : "RL-5";
    private static readonly TimeSpan GameTimeLimit = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Who fills the other five seats. The heuristics by default; JEV_OPPONENTS takes a comma-separated
    /// list, which is how the hard table gets played - RL-5 beats the heuristics 21 games in 24 by an
    /// average of 46 points, and nothing can be shown to improve a bot in games it has already won.
    /// </summary>
    private static string[] Opponents => Environment.GetEnvironmentVariable("JEV_OPPONENTS") is { Length: > 0 } list
        ? list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        : ["Default", "Aggressive", "Greedy", "Friendly", "Random"];

    private sealed record Played(int Score, int Rank, int Power, int Turns, bool Finished);

    /// <summary>
    /// A seeded rerun has to reproduce exactly, or the pairing proves nothing. No key needed: the layer
    /// is off in both runs, so this is purely about the game being reproducible.
    /// </summary>
    [Fact]
    public async Task TheSameSeedPlaysTheSameGame()
    {
        foreach (int seed in new[] { 101, 202 })
        {
            var first = await PlaySeeded(seed, client: null);
            var second = await PlaySeeded(seed, client: null);

            _output.WriteLine($"seed {seed}: score {first.Score} then {second.Score}, "
                            + $"{first.Turns} then {second.Turns} turns, power {first.Power} then {second.Power}");
            Assert.True(first.Finished && second.Finished, "both runs have to finish for the comparison to mean anything");
            Assert.Equal(first.Score, second.Score);
            Assert.Equal(first.Turns, second.Turns);
            Assert.Equal(first.Power, second.Power);
        }
    }

    /// <summary>
    /// The measurement: each seed played twice, with and without the layer, and the difference reported
    /// per seed. Skipped without a key.
    ///
    ///   $env:TypeSafe__ApiKey = "&lt;key&gt;"; $env:JEV_SEEDS = "20"; $env:JEV_BASE = "Default"
    ///   dotnet test --filter "FullyQualifiedName~JevPairedRerunTests" --logger "console;verbosity=detailed"
    /// </summary>
    [Fact]
    public async Task TheSameGameWithAndWithoutTheLayer()
    {
        string? key = Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(key))
        {
            _output.WriteLine($"{ApiKeyEnvironmentVariable} is not set - no games were played.");
            return;
        }

        int seeds = int.TryParse(Environment.GetEnvironmentVariable("JEV_SEEDS"), out int requested) && requested > 0 ? requested : 10;
        double gate = double.TryParse(Environment.GetEnvironmentVariable("JEV_GATE"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double configured)
            ? configured
            : TypeSafeClient.DefaultMinimumConfidence;
        string? decisions = Environment.GetEnvironmentVariable("JEV_DECISIONS");

        using var httpClient = new HttpClient();
        var counter = new AnswerCounter();
        _counter = counter;
        var client = new TypeSafeClient(key, () => httpClient, counter, gate, null, decisions);

        _output.WriteLine($"base bot: {BaseBot}; gate {gate:0.00}; decisions: {decisions ?? "the default set"}");

        var differences = new List<double>();
        var stopwatch = Stopwatch.StartNew();

        for (int seed = 1; seed <= seeds; seed++)
        {
            var without = await PlaySeeded(seed, client: null);
            var with = await PlaySeeded(seed, client);
            if (!without.Finished || !with.Finished)
            {
                _output.WriteLine($"seed {seed}: ABANDONED (one run did not finish)");
                continue;
            }

            differences.Add(with.Score - without.Score);
            _output.WriteLine($"seed {seed,3}: without {without.Score,4} (rank {without.Rank}) -> with {with.Score,4} (rank {with.Rank})"
                            + $"   difference {with.Score - without.Score,+4}");
        }

        Assert.NotEmpty(differences);
        double mean = differences.Average();
        double deviation = differences.Count > 1
            ? Math.Sqrt(differences.Sum(d => (d - mean) * (d - mean)) / (differences.Count - 1))
            : 0;
        double interval = 1.96 * deviation / Math.Sqrt(differences.Count);

        _output.WriteLine("");
        _output.WriteLine($"=== {differences.Count} seeded pairs, {stopwatch.Elapsed.TotalMinutes:0.0} min ===");
        _output.WriteLine($"the layer was ahead in {differences.Count(d => d > 0)}, behind in {differences.Count(d => d < 0)}, "
                        + $"level in {differences.Count(d => d == 0)}");
        _output.WriteLine($"average difference on the same game: {mean:+0.00;-0.00;0.00} +/- {interval:0.00} (95%), spread {deviation:0.0}");
        _output.WriteLine(Math.Abs(mean) > interval
            ? "  -> the interval excludes zero: this is a difference, not noise."
            : $"  -> still spans zero. About {Math.Ceiling(Math.Pow(1.96 * deviation / Math.Max(Math.Abs(mean), 0.5), 2)):0} pairs would settle a gap this size.");
        _output.WriteLine($"{counter.Answers} answers, {counter.Overrides} decisions changed, {counter.InputTokens / Math.Max(counter.Answers, 1)} input tokens each");
    }

    /// <summary>
    /// One game on a fixed seed: the same six seats, the same deal, the same random stream. The seat
    /// under test is the first one dealt; <paramref name="client"/> null plays it as the plain bot.
    /// </summary>
    private AnswerCounter? _counter;

    private async Task<Played> PlaySeeded(int seed, TypeSafeClient? client)
    {
        string databaseName = $"JevPaired_{seed}_{(client == null ? "plain" : "layered")}_{Guid.NewGuid():N}";
        var gameId = DeterministicGuid(seed, 0);
        var playerIds = Enumerable.Range(0, 6).Select(index => DeterministicGuid(seed, index + 1)).OrderBy(id => id).ToList();

        var deal = new Random(seed * 31 + 7);
        var nations = Enum.GetValues<Nation>().OrderBy(_ => deal.Next()).ToList();
        var seats = playerIds.OrderBy(_ => deal.Next()).ToList();

        await using (var context = CreateContext(databaseName))
        {
            context.Games.Add(new Game
            {
                Id = gameId,
                Name = $"Paired rerun {seed}",
                MaxPlayers = 6,
                Status = GameStatus.Lobby,
                CurrentTurnNation = Nation.Russia
            });
            context.Players.AddRange(playerIds.Select((id, index) => new Player
            {
                Id = id,
                GameId = gameId,
                UserId = $"paired-{id:N}",
                IsHost = index == 0,
                IsBot = false,
                BotName = $"Seat {index}"
            }));
            await context.SaveChangesAsync();

            await GameSetupHelper.InitializeGameAsync(context, gameId,
                nations.Select((nation, index) => (nation, player: seats[index]))
                       .ToDictionary(entry => entry.nation, entry => entry.player));

            var others = Opponents;
            var players = await context.Players.Where(p => p.GameId == gameId).ToListAsync();
            int other = 0;
            foreach (var player in players)
            {
                player.IsBot = true;
                player.BotType = player.Id == seats[0]
                    ? (client == null ? BaseBot : JevAdviser.BotTypePrefix + BaseBot)
                    : others[other++ % others.Length];
                player.BotName = $"{player.BotType} seat";
            }
            await context.SaveChangesAsync();
        }

        var botService = CreateBotService(databaseName, client, _counter);
        botService.SkipDelays = true;

        // The seed covers every bot decision in this game, so the rerun walks the same path until the
        // layer changes something.
        using (BotRandom.Use(seed))
        {
            botService.TriggerBotTurn(gameId, 0);

            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < GameTimeLimit)
            {
                await Task.Delay(25);
                await using var snapshot = CreateContext(databaseName);
                if (await snapshot.Games.AsNoTracking().Where(g => g.Id == gameId).Select(g => g.Status).SingleAsync() == GameStatus.Finished) break;
            }
        }

        await using var final = CreateContext(databaseName);
        var finished = await final.Games.AsNoTracking()
            .Include(g => g.Players).Include(g => g.NationStates).Include(g => g.Bonds)
            .AsSplitQuery().SingleAsync(g => g.Id == gameId);

        var measured = finished.Players.Single(p => p.Id == seats[0]);
        var ranked = finished.GetRankedPlayers();
        return new Played(
            finished.CalculateScore(measured.Id),
            ranked.FindIndex(p => p.Id == measured.Id) + 1,
            finished.NationStates.Where(ns => ns.ControllerId == measured.Id).Sum(ns => ns.Power),
            finished.TurnCount,
            finished.Status == GameStatus.Finished);
    }

    private sealed class AnswerCounter : ILogger<BotService>
    {
        public int Answers { get; private set; }
        public int Overrides { get; private set; }
        public int InputTokens { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            string message = formatter(state, exception);
            if (message.Contains(" in + ") && message.Contains(" out tokens"))
            {
                Answers++;
                var words = message.Split(' ');
                int at = Array.IndexOf(words, "in");
                if (at > 0 && int.TryParse(words[at - 1], out int tokens)) InputTokens += tokens;
            }
            else if (message.Contains(" instead of ")) Overrides++;
        }
    }

    private static Guid DeterministicGuid(int seed, int discriminator)
    {
        var bytes = new byte[16];
        BitConverter.GetBytes(seed).CopyTo(bytes, 0);
        BitConverter.GetBytes(discriminator).CopyTo(bytes, 4);
        return new Guid(bytes);
    }

    private static ApplicationDbContext CreateContext(string databaseName) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(databaseName).Options);

    private static BotService CreateBotService(string databaseName, TypeSafeClient? client, ILogger<BotService>? logger = null)
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
            provider.Setup(p => p.GetService(typeof(ApplicationDbContext))).Returns(() => CreateContext(databaseName));
            provider.Setup(p => p.GetService(typeof(INotificationService))).Returns(new Mock<INotificationService>().Object);
            scope.Setup(s => s.ServiceProvider).Returns(provider.Object);
            return scope.Object;
        });

        var strategies = new List<IBotStrategy>
        {
            new RandomBotStrategy(), new DefaultBotStrategy(), new GreedyBotStrategy(),
            new AggressiveBotStrategy(), new FriendlyBotStrategy()
        };

        // The counter is the bot loop's logger as well as the client's, or the "decisions changed" line
        // counts nothing: that message comes from BotService, not from the client.
        return new BotService(scopeFactory.Object, hub.Object, strategies, logger ?? NullLogger<BotService>.Instance, client);
    }
}
