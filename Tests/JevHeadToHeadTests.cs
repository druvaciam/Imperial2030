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
/// Does the layer win more games than the model it wraps? The two sit at the same table - one seat plays
/// Jev-RL-5, another plays RL-5, the other four are the heuristics - so both meet the same board, the
/// same opponents and the same dice. Each pairing is played twice with the two seats swapped, which
/// cancels the advantage of whichever nations they were dealt.
///
/// Everything runs through the production path: BotService is given a real TypeSafeClient and builds the
/// layered strategy itself, exactly as the server does.
///
/// Skipped unless a key is in the environment - it needs the network and spends roughly 20 calls per
/// game (one per rondel move of the layered seat):
///
///   $env:TypeSafe__ApiKey = "&lt;key&gt;"; $env:JEV_H2H_PAIRS = "8"
///   dotnet test --filter "FullyQualifiedName~JevHeadToHeadTests" --logger "console;verbosity=detailed"
/// </summary>
[Collection(RlTrainingFlagCollection.Name)]
public class JevHeadToHeadTests
{
    private readonly ITestOutputHelper _output;
    public JevHeadToHeadTests(ITestOutputHelper output) => _output = output;

    private const string ApiKeyEnvironmentVariable = "TypeSafe__ApiKey";
    private const string PairsEnvironmentVariable = "JEV_H2H_PAIRS";
    private const string GateEnvironmentVariable = "JEV_GATE";
    private const string AskAlwaysEnvironmentVariable = "JEV_ASK_ALWAYS";
    private const string DecisionsEnvironmentVariable = "JEV_DECISIONS";
    /// <summary>
    /// Which bot the layer is measured on top of. "RL-5" asks whether the advice can improve a trained
    /// model; "Default" asks whether it is worth anything at all, since a bot playing by a handful of
    /// hand-written rules has plenty of room to be improved. JEV_BASE picks it.
    /// </summary>
    private static string Model => Environment.GetEnvironmentVariable("JEV_BASE") is { Length: > 0 } chosen ? chosen : "RL-5";
    private static string Layered => JevAdviser.BotTypePrefix + Model;

    /// <summary>The other four seats. Never the bot under test, so the two measured seats are the comparison.</summary>
    private static string[] Heuristics => Model.Equals("Default", StringComparison.OrdinalIgnoreCase)
        ? ["Aggressive", "Greedy", "Friendly", "Random"]
        : ["Default", "Aggressive", "Greedy", "Friendly"];
    private const int DefaultPairs = 4;
    private static readonly TimeSpan HardTimeout = TimeSpan.FromMinutes(30);

    private int _abandoned;

    private sealed record Outcome(int Pair, int Half, string BotType, int Score, int Rank, int Power, int Cash, int Turns);

    /// <summary>Counts what the layer did, from the lines the production path already logs.</summary>
    private sealed class OverrideCounter : ILogger<BotService>
    {
        public int Overrides { get; private set; }
        public int LowConfidence { get; private set; }
        public int Failures { get; private set; }
        public int Answers { get; private set; }
        public int InputTokens { get; private set; }
        public readonly Dictionary<string, int> ByQuestion = new();

        /// <summary>
        /// Anything the bot loop logged as an error. Without this a bot turn that throws just stops the
        /// game, and the run looks like an unexplained timeout - which is how two runs were lost.
        /// </summary>
        public readonly List<string> Errors = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            string message = formatter(state, exception);
            if (logLevel >= LogLevel.Error)
            {
                lock (Errors) { if (Errors.Count < 20) Errors.Add($"{message} {exception}"); }
                return;
            }
            if (message.Contains(" in + ") && message.Contains(" out tokens"))
            {
                Answers++;
                // "... 1591 in + 83 out tokens": the spend, as the service itself reported it.
                var words = message.Split(' ');
                int at = Array.IndexOf(words, "in");
                if (at > 0 && int.TryParse(words[at - 1], out int tokens)) InputTokens += tokens;
                // "<model> answered <question id> with ...": which decision the call was spent on.
                if (words.Length > 2) ByQuestion[words[2]] = ByQuestion.GetValueOrDefault(words[2]) + 1;
                return;
            }
            if (message.Contains(" instead of ")) Overrides++;
            else if (message.Contains("keeping the policy's space")) LowConfidence++;
            else if (message.StartsWith("TypeSafe ") || message.StartsWith("Jev named")) Failures++;
        }
    }

    [Fact]
    public async Task TheLayeredBotIsComparedWithTheModelItWrapsAtTheSameTable()
    {
        string? key = Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(key))
        {
            _output.WriteLine($"{ApiKeyEnvironmentVariable} is not set - no games were played.");
            return;
        }

        int pairs = int.TryParse(Environment.GetEnvironmentVariable(PairsEnvironmentVariable), out int requested) && requested > 0
            ? requested
            : DefaultPairs;

        using var httpClient = new HttpClient();
        var counter = new OverrideCounter();
        double gate = double.TryParse(Environment.GetEnvironmentVariable(GateEnvironmentVariable),
                                      System.Globalization.NumberStyles.Float,
                                      System.Globalization.CultureInfo.InvariantCulture, out double configured)
            ? configured
            : TypeSafeClient.DefaultMinimumConfidence;
        bool askOnEveryMove = Environment.GetEnvironmentVariable(AskAlwaysEnvironmentVariable) == "1";
        string? decisions = Environment.GetEnvironmentVariable(DecisionsEnvironmentVariable);
        var client = new TypeSafeClient(key, () => httpClient, counter, gate, askOnEveryMove, decisions);
        _output.WriteLine($"base bot: {Model}, layered as {Layered}");
        _output.WriteLine($"gate: an answer is taken at {gate:0.00} confidence or more; "
                        + (askOnEveryMove ? "every rondel move is asked about" : "only questionable moves are asked about")
                        + $"; decisions: {decisions ?? "all"}");
        var outcomes = new List<Outcome>();
        var stopwatch = Stopwatch.StartNew();

        _abandoned = 0;
        for (int pair = 1; pair <= pairs && stopwatch.Elapsed < HardTimeout; pair++)
        {
            // Same deal twice, the two model seats swapped: whatever the first nation is worth, each side
            // plays it once.
            foreach (int half in new[] { 0, 1 })
            {
                var played = await PlayGame($"JevH2H_{pair}_{half}", pair, half, client, counter);
                if (played.Count == 0) continue; // abandoned; already reported
                outcomes.AddRange(played);
                var layered = played.Single(o => o.BotType == Layered);
                var plain = played.Single(o => o.BotType == Model);
                _output.WriteLine($"pair {pair} half {half}: {Layered} score {layered.Score} (rank {layered.Rank}), "
                                + $"{Model} score {plain.Score} (rank {plain.Rank}), {layered.Turns} turns");
            }
        }

        Report(outcomes, counter, stopwatch.Elapsed);
        Assert.NotEmpty(outcomes);
    }

    /// <summary>
    /// Which two nations the model seats play, drawn per pair and the same in both halves: the seats swap
    /// between the halves, so each side plays each of the two once and the nations cancel out. Drawn at
    /// all because a fixed pair of nations would only ever compare the two bots at those nations.
    /// </summary>
    private static List<Nation> DealOrder(int pair)
    {
        var random = new Random(pair * 7919);
        return Enum.GetValues<Nation>().OrderBy(_ => random.Next()).ToList();
    }

    /// <summary>How long a game may take before it is abandoned and left out of the comparison.</summary>
    private static readonly TimeSpan GameTimeLimit = TimeSpan.FromMinutes(3);

    private async Task<List<Outcome>> PlayGame(string databaseName, int pair, int half, TypeSafeClient client, OverrideCounter counter)
    {
        databaseName = $"{databaseName}_{Guid.NewGuid():N}";
        var gameId = Guid.NewGuid();
        var playerIds = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).OrderBy(id => id).ToList();

        await using (var context = CreateContext(databaseName))
        {
            context.Games.Add(new Game
            {
                Id = gameId,
                Name = $"Jev head to head {pair}.{half}",
                MaxPlayers = 6,
                Status = GameStatus.Lobby,
                CurrentTurnNation = Nation.Russia
            });
            context.Players.AddRange(playerIds.Select((id, index) => new Player
            {
                Id = id,
                GameId = gameId,
                UserId = $"jev-h2h-{id:N}",
                IsHost = index == 0,
                IsBot = false,
                BotName = $"Seat {index}"
            }));
            await context.SaveChangesAsync();

            var deal = playerIds.OrderBy(_ => Random.Shared.Next()).ToList();
            var dealtNations = DealOrder(pair);
            await GameSetupHelper.InitializeGameAsync(context, gameId,
                dealtNations.Select((nation, index) => (nation, player: deal[index]))
                            .ToDictionary(entry => entry.nation, entry => entry.player));

            // Seats 0 and 1 of the deal are the two model seats, swapped between the halves.
            var players = await context.Players.Where(p => p.GameId == gameId).ToListAsync();
            int heuristic = 0;
            foreach (var player in players)
            {
                player.IsBot = true;
                player.BotType = player.Id == deal[0] ? (half == 0 ? Layered : Model)
                               : player.Id == deal[1] ? (half == 0 ? Model : Layered)
                               : Heuristics[heuristic++ % Heuristics.Length];
                player.BotName = $"{player.BotType} seat";
            }
            await context.SaveChangesAsync();
        }

        var botService = CreateBotService(databaseName, client, counter);
        botService.SkipDelays = true;
        botService.TriggerBotTurn(gameId, 0);

        var clock = Stopwatch.StartNew();
        bool over = false;
        while (clock.Elapsed < GameTimeLimit)
        {
            await Task.Delay(25);
            await using var snapshot = CreateContext(databaseName);
            if (await snapshot.Games.AsNoTracking().Where(g => g.Id == gameId).Select(g => g.Status).SingleAsync() == GameStatus.Finished)
            {
                over = true;
                break;
            }
        }

        await using var final = CreateContext(databaseName);
        var finished = await final.Games.AsNoTracking()
            .Include(g => g.Players).Include(g => g.NationStates).Include(g => g.Bonds)
            .AsSplitQuery().SingleAsync(g => g.Id == gameId);

        // A game that stops moving is left out rather than failing the run: the comparison is between two
        // bots over many games, and one stalled game is a data point lost, not a result. It is counted
        // and reported, because a stall is still a bug worth chasing.
        if (!over)
        {
            _abandoned++;
            _output.WriteLine($"pair {pair} half {half}: ABANDONED after {GameTimeLimit.TotalMinutes:0} minutes at turn {finished.TurnCount}"
                            + (counter.Errors.Count > 0 ? $" - last error: {counter.Errors[^1]}" : " with no error logged"));
            return new List<Outcome>();
        }

        var ranked = finished.GetRankedPlayers();
        return finished.Players
            .Where(player => player.BotType == Layered || player.BotType == Model)
            .Select(player => new Outcome(
                pair,
                half,
                player.BotType!,
                finished.CalculateScore(player.Id),
                ranked.FindIndex(p => p.Id == player.Id) + 1,
                finished.NationStates.Where(ns => ns.ControllerId == player.Id).Sum(ns => ns.Power),
                player.Cash,
                finished.TurnCount))
            .ToList();
    }

    private void Report(IReadOnlyList<Outcome> outcomes, OverrideCounter counter, TimeSpan elapsed)
    {
        _output.WriteLine("");
        _output.WriteLine($"=== {outcomes.Count / 2} games, {elapsed.TotalMinutes:0.0} min"
                        + (_abandoned > 0 ? $", {_abandoned} abandoned" : "") + " ===");
        _output.WriteLine($"{"bot",-12} {"games",6} {"wins",6} {"avg score",10} {"avg rank",9} {"avg power",10} {"avg cash",9}");

        foreach (string botType in new[] { Layered, Model })
        {
            var rows = outcomes.Where(o => o.BotType == botType).ToList();
            _output.WriteLine($"{botType,-12} {rows.Count,6} {rows.Count(o => o.Rank == 1),6} "
                            + $"{rows.Average(o => o.Score),10:0.00} {rows.Average(o => o.Rank),9:0.00} "
                            + $"{rows.Average(o => o.Power),10:0.0} {rows.Average(o => o.Cash),9:0.0}");
        }

        // Paired: within each game, which of the two finished ahead. Half the noise of comparing averages.
        var games = outcomes.GroupBy(o => (o.Pair, o.Half)).ToList();
        int layeredAhead = games.Count(g => g.Single(o => o.BotType == Layered).Score > g.Single(o => o.BotType == Model).Score);
        int plainAhead = games.Count(g => g.Single(o => o.BotType == Layered).Score < g.Single(o => o.BotType == Model).Score);
        double averageGap = games.Average(g => g.Single(o => o.BotType == Layered).Score - g.Single(o => o.BotType == Model).Score);

        // Game-to-game scores swing by tens of points, so the average on its own says very little. The
        // interval is what decides whether a run has found anything: if it spans zero, it has not.
        var gaps = games.Select(g => (double)(g.Single(o => o.BotType == Layered).Score - g.Single(o => o.BotType == Model).Score)).ToList();
        double deviation = gaps.Count > 1
            ? Math.Sqrt(gaps.Sum(gap => (gap - averageGap) * (gap - averageGap)) / (gaps.Count - 1))
            : 0;
        double standardError = gaps.Count > 0 ? deviation / Math.Sqrt(gaps.Count) : 0;

        _output.WriteLine("");
        _output.WriteLine($"head to head in the same game: {Layered} ahead {layeredAhead}, {Model} ahead {plainAhead}, "
                        + $"level {games.Count - layeredAhead - plainAhead}");
        _output.WriteLine($"average score difference (layered minus plain): {averageGap:+0.00;-0.00;0.00} "
                        + $"+/- {1.96 * standardError:0.00} (95%), per-game spread {deviation:0.0}");
        _output.WriteLine(Math.Abs(averageGap) > 1.96 * standardError
            ? "  -> the interval excludes zero: this run found a difference."
            : $"  -> the interval spans zero: no difference shown. About {Math.Ceiling(Math.Pow(1.96 * deviation / Math.Max(Math.Abs(averageGap), 0.5), 2)):0} games would be needed to resolve a gap this size.");
        _output.WriteLine($"{counter.Answers} answers over {games.Count} games ({(double)counter.Answers / Math.Max(games.Count, 1):0.0} a game, "
                        + $"{counter.InputTokens / Math.Max(counter.Answers, 1)} input tokens each): the layer changed the decision "
                        + $"{counter.Overrides} times, kept it for low confidence {counter.LowConfidence} times, "
                        + $"and failed to get an answer {counter.Failures} times");
        if (counter.Errors.Count > 0)
        {
            _output.WriteLine("");
            _output.WriteLine($"the bot loop logged {counter.Errors.Count} error(s):");
            foreach (string error in counter.Errors.Take(5)) _output.WriteLine($"  {error}");
        }

        foreach (var question in counter.ByQuestion.OrderByDescending(entry => entry.Value))
        {
            _output.WriteLine($"  {question.Key,-24} {question.Value,5} ({(double)question.Value / Math.Max(games.Count, 1):0.0} a game)");
        }
    }

    private static ApplicationDbContext CreateContext(string databaseName) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(databaseName).Options);

    private static BotService CreateBotService(string databaseName, TypeSafeClient client, ILogger<BotService> logger)
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

        // The layered and plain model strategies are built by BotService from the bot type, as in production.
        return new BotService(scopeFactory.Object, hub.Object, strategies, logger, client);
    }
}
