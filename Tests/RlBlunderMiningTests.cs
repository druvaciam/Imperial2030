using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Imperial2030.Server.Data;
using Imperial2030.Server.Helpers;
using Imperial2030.Server.Hubs;
using Imperial2030.Server.Models;
using Imperial2030.Server.Services;
using Imperial2030.Server.Services.Bots;
using Imperial2030.Server.Services.Bots.Strategies;
using Imperial2030.Shared.Constants;
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
/// Where the model actually loses games. Plays seeded self-play games, records every rondel decision the
/// model makes with the position it was made in, then reports the moves from its worst games - the ones
/// where it finished last or far behind.
///
/// No API calls: this is about the model, not the layer. What it produces is a list of positions worth
/// a second opinion, which is a better target for one than "every decision" - and a list worth reading
/// on its own, because a move the model keeps getting wrong in its worst games is a training signal.
///
///   dotnet test --filter "FullyQualifiedName~RlBlunderMiningTests" --logger "console;verbosity=detailed"
///   $env:JEV_BASE = "RL-5"; $env:MINING_GAMES = "30"
/// </summary>
[Collection(RlTrainingFlagCollection.Name)]
public class RlBlunderMiningTests
{
    private readonly ITestOutputHelper _output;
    public RlBlunderMiningTests(ITestOutputHelper output) => _output = output;

    private static string Model => Environment.GetEnvironmentVariable("JEV_BASE") is { Length: > 0 } chosen ? chosen : "RL-5";
    private static readonly TimeSpan GameTimeLimit = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Who fills the other five seats. The heuristics by default; JEV_OPPONENTS takes a comma-separated
    /// list, which is how the hard table gets played - RL-5 beats the heuristics 21 games in 24 by an
    /// average of 46 points, and nothing can be shown to improve a bot in games it has already won.
    /// </summary>
    private static string[] Opponents => Environment.GetEnvironmentVariable("JEV_OPPONENTS") is { Length: > 0 } list
        ? list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        : ["Default", "Aggressive", "Greedy", "Friendly", "Random"];

    /// <summary>One rondel decision, with everything needed to judge it after the game is over.</summary>
    private sealed record Move(
        int Game, int Turn, Nation Nation, int From, int To, int MoveCost, int Power, int Treasury,
        bool Wasted, bool SkippedTaxation, int TaxationPowerForgone, bool DearerTwin, int Units, int Flags);

    private static readonly ConcurrentBag<Move> Moves = new();
    private static int _currentGame;

    /// <summary>The model, with every rondel decision it makes written down as it makes it.</summary>
    private sealed class RecordingModel : RLBotStrategy
    {
        public const string BotType = "RecordedModel";
        public override string Name => BotType;

        public RecordingModel() : base(Model) { }

        public override Task<int?> ReconsiderRondelSlotAsync(Game game, NationState ns, Player controller,
                                                             IReadOnlyList<RondelCandidate> candidates, int scoringPick)
        {
            if (ns.RondelPosition.HasValue)
            {
                int from = ns.RondelPosition.Value;
                var taxation = TaxationHelper.PreviewTaxation(game, ns);
                Moves.Add(new Move(
                    _currentGame, game.TurnCount, ns.Nation, from, scoringPick,
                    RondelData.GetMoveCost(from, scoringPick, ns.Power),
                    ns.Power, ns.Treasury,
                    RondelOutlookHelper.IsWastedRondelSpace(game, ns, scoringPick),
                    RondelOutlookHelper.SkippedValuableTaxation(from, scoringPick, taxation.ExpectedPowerGain),
                    taxation.ExpectedPowerGain,
                    RondelOutlookHelper.PaidForTheFartherTwinSlot(from, scoringPick, ns.Power),
                    game.Units.Count(u => u.Nation == ns.Nation),
                    game.TerritoryStates.Count(ts => ts.Controller == ns.Nation)));
            }

            return Task.FromResult<int?>(null); // recording only; the model's move stands
        }
    }

    [Fact]
    public async Task TheModelsWorstGamesAreMinedForTheMovesThatLostThem()
    {
        int games = int.TryParse(Environment.GetEnvironmentVariable("MINING_GAMES"), out int requested) && requested > 0 ? requested : 20;
        Moves.Clear();

        var results = new List<(int Game, int Score, int Rank, int Best, int Turns)>();
        var stopwatch = Stopwatch.StartNew();

        for (int seed = 1; seed <= games; seed++)
        {
            _currentGame = seed;
            var played = await PlaySeeded(seed);
            if (played.HasValue) results.Add((seed, played.Value.Score, played.Value.Rank, played.Value.Best, played.Value.Turns));
        }

        Assert.NotEmpty(results);
        Report(results, stopwatch.Elapsed);
    }

    private void Report(List<(int Game, int Score, int Rank, int Best, int Turns)> results, TimeSpan elapsed)
    {
        var byMargin = results.OrderBy(r => r.Score - r.Best).ToList();
        var worst = byMargin.Take(Math.Max(1, results.Count / 4)).Select(r => r.Game).ToHashSet();
        var best = byMargin.TakeLast(Math.Max(1, results.Count / 4)).Select(r => r.Game).ToHashSet();

        _output.WriteLine($"=== {Model} against {string.Join(", ", Opponents)}: {results.Count} seeded games, "
                        + $"{elapsed.TotalMinutes:0.0} min, {Moves.Count} rondel moves recorded ===");
        _output.WriteLine($"finished first in {results.Count(r => r.Rank == 1)}; "
                        + $"average margin to the best other player {results.Average(r => r.Score - r.Best):+0.0;-0.0}");
        _output.WriteLine("");
        _output.WriteLine($"its worst quarter: games {string.Join(", ", worst.OrderBy(g => g))}");
        _output.WriteLine($"its best quarter:  games {string.Join(", ", best.OrderBy(g => g))}");

        void Rates(string label, ICollection<int> games)
        {
            var moves = Moves.Where(m => games.Contains(m.Game)).ToList();
            if (moves.Count == 0) return;
            _output.WriteLine($"  {label,-18} {moves.Count,5} moves | wasted {Percent(moves.Count(m => m.Wasted), moves.Count)} | "
                            + $"skipped a 5+ power taxation {Percent(moves.Count(m => m.SkippedTaxation), moves.Count)} | "
                            + $"paid for the dearer twin {Percent(moves.Count(m => m.DearerTwin), moves.Count)} | "
                            + $"paid to move {Percent(moves.Count(m => m.MoveCost > 0), moves.Count)}");
        }

        _output.WriteLine("");
        _output.WriteLine("how often each kind of bad move appears, worst games against best:");
        Rates("worst quarter", worst);
        Rates("best quarter", best);
        Rates("all games", results.Select(r => r.Game).ToHashSet());

        // The moves themselves, worst games first: these are the positions a second opinion would have
        // to get right to be worth its calls.
        var flagged = Moves.Where(m => worst.Contains(m.Game) && (m.Wasted || m.SkippedTaxation || m.DearerTwin))
            .OrderBy(m => m.Game).ThenBy(m => m.Turn).ToList();

        _output.WriteLine("");
        _output.WriteLine($"the {flagged.Count} flagged moves from its worst games:");
        foreach (var move in flagged.Take(40))
        {
            var reasons = new List<string>();
            if (move.Wasted) reasons.Add($"{RondelData.GetSlotName(move.To)} could do nothing from there");
            if (move.SkippedTaxation) reasons.Add($"walked past a Taxation worth {move.TaxationPowerForgone} power");
            if (move.DearerTwin) reasons.Add($"paid {move.MoveCost}m for the farther of two identical spaces");
            _output.WriteLine($"  game {move.Game,2} turn {move.Turn,3} {move.Nation,-6} "
                            + $"{RondelData.GetSlotName(move.From),-10} -> {RondelData.GetSlotName(move.To),-10} "
                            + $"(power {move.Power,2}, treasury {move.Treasury,2}m, {move.Units} units, {move.Flags} flags): "
                            + string.Join("; ", reasons));
        }

        WriteCsv();
    }

    private static string Percent(int part, int whole) => $"{100.0 * part / whole:0.0}% ({part})";

    private void WriteCsv()
    {
        string path = Path.Combine(Path.GetTempPath(), $"rl-moves-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        using var writer = new StreamWriter(path);
        writer.WriteLine("game,turn,nation,from,to,moveCost,power,treasury,wasted,skippedTaxation,taxationPowerForgone,dearerTwin,units,flags");
        foreach (var m in Moves.OrderBy(m => m.Game).ThenBy(m => m.Turn))
        {
            writer.WriteLine($"{m.Game},{m.Turn},{m.Nation},{RondelData.GetSlotName(m.From)},{RondelData.GetSlotName(m.To)},"
                           + $"{m.MoveCost},{m.Power},{m.Treasury},{m.Wasted},{m.SkippedTaxation},{m.TaxationPowerForgone},"
                           + $"{m.DearerTwin},{m.Units},{m.Flags}");
        }
        _output.WriteLine("");
        _output.WriteLine($"every recorded move: {path}");
    }

    private async Task<(int Score, int Rank, int Best, int Turns)?> PlaySeeded(int seed)
    {
        string databaseName = $"Mining_{seed}_{Guid.NewGuid():N}";
        var gameId = Guid.NewGuid();
        var playerIds = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).OrderBy(id => id).ToList();

        var deal = new Random(seed * 31 + 7);
        var nations = Enum.GetValues<Nation>().OrderBy(_ => deal.Next()).ToList();
        var seats = playerIds.OrderBy(_ => deal.Next()).ToList();

        await using (var context = CreateContext(databaseName))
        {
            context.Games.Add(new Game
            {
                Id = gameId, Name = $"Mining {seed}", MaxPlayers = 6,
                Status = GameStatus.Lobby, CurrentTurnNation = Nation.Russia
            });
            context.Players.AddRange(playerIds.Select((id, index) => new Player
            {
                Id = id, GameId = gameId, UserId = $"mining-{id:N}", IsHost = index == 0, IsBot = false, BotName = $"Seat {index}"
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
                player.BotType = player.Id == seats[0] ? RecordingModel.BotType : others[other++ % others.Length];
                player.BotName = $"{player.BotType} seat";
            }
            await context.SaveChangesAsync();
        }

        var botService = CreateBotService(databaseName);
        botService.SkipDelays = true;

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
        if (finished.Status != GameStatus.Finished) return null;

        var measured = finished.Players.Single(p => p.Id == seats[0]);
        int score = finished.CalculateScore(measured.Id);
        int best = finished.Players.Where(p => p.Id != measured.Id).Max(p => finished.CalculateScore(p.Id));
        return (score, finished.GetRankedPlayers().FindIndex(p => p.Id == measured.Id) + 1, best, finished.TurnCount);
    }

    private static ApplicationDbContext CreateContext(string databaseName) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(databaseName).Options);

    private static BotService CreateBotService(string databaseName)
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
            new AggressiveBotStrategy(), new FriendlyBotStrategy(), new RecordingModel()
        };

        return new BotService(scopeFactory.Object, hub.Object, strategies, NullLogger<BotService>.Instance);
    }
}
