using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Imperial2030.Server.Data;
using Imperial2030.Server.Helpers;
using Imperial2030.Server.Hubs;
using Imperial2030.Server.Models;
using Imperial2030.Server.Services;
using Imperial2030.Server.Services.Bots;
using Imperial2030.Server.Services.Bots.Strategies;
using Imperial2030.Server.Services.TypeSafe;
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
/// How good Jev's rondel answers actually are, measured on real positions instead of argued about.
///
/// The layer plays shadow here: the bot moves where RL-5 says, and Jev is asked the same question about
/// the same position anyway. Every answer is scored against the position it was asked about, with the
/// predicates the training server already uses - <see cref="RondelOutlookHelper.IsWastedRondelSpace"/> (the
/// space's action can do nothing from here), <see cref="TcpTrainingServer.SkippedValuableTaxation"/> and
/// <see cref="TcpTrainingServer.PaidForTheFartherTwinSlot"/>. That gives two numbers worth having:
/// how often Jev repairs a move the model got wrong, and how often it breaks one the model got right.
///
/// Shadow play, not head-to-head, because it holds the position fixed: both answers are to the same
/// question, so the comparison is not swamped by the two games drifting apart. The confidence and the
/// full distribution are recorded per decision, so a gate can be re-tuned over this data without
/// spending another call.
///
/// Skipped unless a key is in the environment - it needs the network and spends quota:
///
///   $env:TypeSafe__ApiKey = "&lt;key&gt;"; $env:JEV_CALL_BUDGET = "120"
///   dotnet test --filter "FullyQualifiedName~JevDecisionQualityTests" --logger "console;verbosity=detailed"
/// </summary>
[Collection(RlTrainingFlagCollection.Name)]
public class JevDecisionQualityTests
{
    private readonly ITestOutputHelper _output;
    public JevDecisionQualityTests(ITestOutputHelper output) => _output = output;

    private const string ApiKeyEnvironmentVariable = "TypeSafe__ApiKey";
    private const string BudgetEnvironmentVariable = "JEV_CALL_BUDGET";
    private const string WrappedModel = "RL-5";
    private const string StrideEnvironmentVariable = "JEV_ASK_EVERY";
    private const string GamesEnvironmentVariable = "JEV_GAMES";
    private const int DefaultCallBudget = 60;
    private const int DefaultGames = 5;
    private static readonly TimeSpan HardTimeout = TimeSpan.FromMinutes(25);

    /// <summary>One rondel decision: what the model chose, what Jev answered, and how each scores.</summary>
    private sealed record Decision(
        Nation Nation,
        int FromSlot,
        int ModelSlot,
        int JevSlot,
        double Confidence,
        double ModelProbability,
        double JevProbability,
        bool ModelWasted,
        bool JevWasted,
        bool ModelSkippedTaxation,
        bool JevSkippedTaxation,
        bool ModelPaidForTheFartherTwin,
        bool JevPaidForTheFartherTwin,
        int OptionCount,
        int Turn,
        int Power)
    {
        public bool Disagreed => JevSlot != ModelSlot;
        public bool ModelErred => ModelWasted || ModelSkippedTaxation || ModelPaidForTheFartherTwin;
        public bool JevErred => JevWasted || JevSkippedTaxation || JevPaidForTheFartherTwin;
    }

    private static readonly ConcurrentBag<Decision> Decisions = new();
    private static int _callsLeft;
    private static int _decisionsSeen;

    /// <summary>
    /// Ask about one decision in this many. The budget then spreads over whole games instead of being
    /// spent on the opening of the first few, where a nation has one factory, no flags and no taxation
    /// worth skipping - the positions where the question matters least.
    /// </summary>
    private static int _stride = 1;

    /// <summary>
    /// RL-5, asked for Jev's opinion on every rondel move and then playing its own anyway. The answer is
    /// recorded, never acted on, so the game stays on the model's trajectory and both arms of the
    /// comparison see exactly the same positions.
    /// </summary>
    /// <summary>
    /// The adviser, with every answer written down before the strategy acts on it: what was asked, what
    /// came back, and how both the answer and the model's own move score against the position.
    /// </summary>
    private sealed class RecordingAdviser : JevAdviser
    {
        public RecordingAdviser(TypeSafeClient client) : base(client) { }

        protected override void OnAnswered(Game game, NationState ns, Player controller,
                                           IReadOnlyDictionary<string, RondelCandidate> options, int scoringPick, ChoiceAnswer answer)
        {
            if (!options.TryGetValue(answer.Choice, out var jev)) return;

            int from = ns.RondelPosition!.Value;
            int taxPower = TaxationHelper.PreviewTaxation(game, ns).ExpectedPowerGain;
            string modelOption = options.Where(entry => entry.Value.Slot == scoringPick).Select(entry => entry.Key).FirstOrDefault() ?? "";

            Decisions.Add(new Decision(
                ns.Nation,
                from,
                scoringPick,
                jev.Slot,
                answer.Confidence,
                modelOption.Length == 0 ? 0 : answer.Probabilities.GetValueOrDefault(modelOption),
                answer.Probabilities.GetValueOrDefault(answer.Choice),
                RondelOutlookHelper.IsWastedRondelSpace(game, ns, scoringPick),
                RondelOutlookHelper.IsWastedRondelSpace(game, ns, jev.Slot),
                TcpTrainingServer.SkippedValuableTaxation(from, scoringPick, taxPower),
                TcpTrainingServer.SkippedValuableTaxation(from, jev.Slot, taxPower),
                TcpTrainingServer.PaidForTheFartherTwinSlot(from, scoringPick, ns.Power),
                TcpTrainingServer.PaidForTheFartherTwinSlot(from, jev.Slot, ns.Power),
                options.Count,
                game.TurnCount,
                ns.Power));
        }
    }

    /// <summary>
    /// RL-5, asked for Jev's opinion on every rondel move and then playing its own anyway. The answer is
    /// recorded, never acted on, so the game stays on the model's trajectory and both arms of the
    /// comparison see exactly the same positions.
    /// </summary>
    private sealed class ShadowJev : JevRlBotStrategy
    {
        public const string Name_ = "ShadowJev";

        // Its own bot type, over the RL-5 model: BotService resolves a registered strategy by name, and
        // a "Jev-..." name would make it build a second, unrecorded instance instead.
        public ShadowJev(TypeSafeClient client) : base(Name_, WrappedModel, new RecordingAdviser(client)) { }

        public static int Questionable;

        public override async Task<int?> ReconsiderRondelSlotAsync(Game game, NationState ns, Player controller,
                                                                   IReadOnlyList<RondelCandidate> candidates, int scoringPick)
        {
            int seen = Interlocked.Increment(ref _decisionsSeen);
            if (RondelOutlookHelper.MoveLooksQuestionable(game, ns, scoringPick,
                    JevAdviser.ReconsiderableOptions(candidates).Values.Select(option => option.Slot)))
            {
                Interlocked.Increment(ref Questionable);
            }
            if (seen % _stride != 0) return null;                        // sampled out
            if (Interlocked.Decrement(ref _callsLeft) < 0) return null;  // budget spent; the game plays on

            await base.ReconsiderRondelSlotAsync(game, ns, controller, candidates, scoringPick);
            return null; // shadow: the model's move is the one that gets played
        }
    }

    [Fact]
    public async Task JevsAnswersAreScoredAgainstTheModelsOnRealPositions()
    {
        string? key = Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(key))
        {
            _output.WriteLine($"{ApiKeyEnvironmentVariable} is not set - no calls were made.");
            return;
        }

        _callsLeft = int.TryParse(Environment.GetEnvironmentVariable(BudgetEnvironmentVariable), out int budget) ? budget : DefaultCallBudget;
        _stride = int.TryParse(Environment.GetEnvironmentVariable(StrideEnvironmentVariable), out int stride) && stride > 0 ? stride : 1;
        int games = int.TryParse(Environment.GetEnvironmentVariable(GamesEnvironmentVariable), out int count) && count > 0 ? count : DefaultGames;
        Decisions.Clear();
        _decisionsSeen = 0;
        ShadowJev.Questionable = 0;
        int requested = _callsLeft;

        using var httpClient = new HttpClient();
        // askOnEveryMove: the shadow measures answers on every position, including the ones the live
        // layer now skips, so the two populations can be compared.
        var shadow = new ShadowJev(new TypeSafeClient(key, () => httpClient, null, null, askOnEveryMove: true));
        var stopwatch = Stopwatch.StartNew();

        for (int game = 1; game <= games && stopwatch.Elapsed < HardTimeout; game++)
        {
            bool finished = await PlayOneGame(game, shadow);
            _output.WriteLine($"game {game}: {(finished ? "finished" : "TIMED OUT")}, {_decisionsSeen} rondel moves by the layered seat so far, "
                            + $"{Decisions.Count} answers recorded, {Math.Max(_callsLeft, 0)} calls left");
        }

        Report(Decisions.OrderBy(d => d.Turn).ToList(), requested, stopwatch.Elapsed);
        if (requested > 0) Assert.NotEmpty(Decisions); // a budget of zero is a sizing run: games, no calls
    }

    /// <summary>
    /// One self-play game: the shadow bot on one seat, the heuristics and the plain model on the others,
    /// with the rulebook's deal for six players (p.4-5) through the same setup the real game uses.
    /// </summary>
    private async Task<bool> PlayOneGame(int number, ShadowJev shadow)
    {
        string databaseName = $"JevQuality_{number}_{Guid.NewGuid():N}";
        var gameId = Guid.NewGuid();
        var playerIds = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).OrderBy(id => id).ToList();
        var nations = Enum.GetValues<Nation>();

        await using (var context = CreateContext(databaseName))
        {
            context.Games.Add(new Game
            {
                Id = gameId,
                Name = $"Jev decision quality {number}",
                MaxPlayers = 6,
                Status = GameStatus.Lobby,
                CurrentTurnNation = Nation.Russia
            });
            context.Players.AddRange(playerIds.Select((id, index) => new Player
            {
                Id = id,
                GameId = gameId,
                UserId = $"jev-quality-{id:N}",
                IsHost = index == 0,
                IsBot = false,
                BotName = $"Seat {index}"
            }));
            await context.SaveChangesAsync();

            var shuffled = playerIds.OrderBy(_ => Random.Shared.Next()).ToList();
            await GameSetupHelper.InitializeGameAsync(context, gameId,
                nations.Select((nation, index) => (nation, player: shuffled[index]))
                       .ToDictionary(pair => pair.nation, pair => pair.player));

            // The shadow seat gets the first nation dealt; the rest are the opponents RL-5 was trained
            // against, so the positions it is asked about are the ones it meets in a real game.
            string[] opponents = ["Default", "Aggressive", "Greedy", "Friendly", WrappedModel];
            var players = await context.Players.Where(p => p.GameId == gameId).ToListAsync();
            for (int seat = 0; seat < players.Count; seat++)
            {
                players[seat].IsBot = true;
                players[seat].BotType = players[seat].Id == shuffled[0] ? shadow.Name : opponents[seat % opponents.Length];
                players[seat].BotName = $"{players[seat].BotType} {seat}";
            }
            await context.SaveChangesAsync();
        }

        var botService = CreateBotService(databaseName, shadow);
        botService.SkipDelays = true;
        botService.TriggerBotTurn(gameId, 0);

        // Played to the end even once the budget is gone: a game abandoned in its opening would only ever
        // have been asked about openings.
        var gameClock = Stopwatch.StartNew();
        while (gameClock.Elapsed < TimeSpan.FromMinutes(6))
        {
            await Task.Delay(25);
            await using var snapshot = CreateContext(databaseName);
            var status = await snapshot.Games.AsNoTracking().Where(g => g.Id == gameId).Select(g => g.Status).SingleAsync();
            if (status == GameStatus.Finished) return true;
        }

        return false;
    }

    private void Report(IReadOnlyList<Decision> decisions, int requested, TimeSpan elapsed)
    {
        _output.WriteLine("");
        _output.WriteLine($"=== {decisions.Count} answers out of {_decisionsSeen} rondel moves "
                        + $"({requested} calls budgeted, 1 in {_stride} asked, {elapsed.TotalMinutes:0.0} min) ===");

        var modelErrors = decisions.Where(d => d.ModelErred).ToList();
        var modelClean = decisions.Where(d => !d.ModelErred).ToList();

        _output.WriteLine($"the model's move looked questionable to the code in {ShadowJev.Questionable} of {_decisionsSeen} rondel moves "
                        + $"- the only ones the layer asks about unless TypeSafe:AskOnEveryMove is set");
        _output.WriteLine($"the model's own move was a known mistake in {modelErrors.Count} of {decisions.Count}");
        _output.WriteLine($"  wasted space {decisions.Count(d => d.ModelWasted)}, skipped a 5+ power taxation {decisions.Count(d => d.ModelSkippedTaxation)}, paid for the dearer twin {decisions.Count(d => d.ModelPaidForTheFartherTwin)}");
        _output.WriteLine($"Jev disagreed with it {decisions.Count(d => d.Disagreed)} times, and its own answer was a known mistake {decisions.Count(d => d.JevErred)} times");
        _output.WriteLine($"  wasted space {decisions.Count(d => d.JevWasted)}, skipped a 5+ power taxation {decisions.Count(d => d.JevSkippedTaxation)}, paid for the dearer twin {decisions.Count(d => d.JevPaidForTheFartherTwin)}");

        _output.WriteLine("");
        _output.WriteLine("confidence, by whether the answer differed from the model's move:");
        foreach (var group in new[] { ("agreed", decisions.Where(d => !d.Disagreed).ToList()), ("disagreed", decisions.Where(d => d.Disagreed).ToList()) })
        {
            if (group.Item2.Count == 0) continue;
            var sorted = group.Item2.Select(d => d.Confidence).OrderBy(c => c).ToList();
            _output.WriteLine($"  {group.Item1,-10} n={sorted.Count,3}  min {sorted.First():0.00}  median {sorted[sorted.Count / 2]:0.00}  max {sorted.Last():0.00}");
        }

        // What each gate would have done, computed over the same answers: no further calls needed.
        _output.WriteLine("");
        _output.WriteLine("if the gate had been set here, acting on the answers above:");
        _output.WriteLine($"  {"gate",-22} {"overrides",9} {"mistakes fixed",15} {"clean moves broken",19} {"net",5}");
        foreach (double threshold in new[] { 0.0, 0.3, 0.4, 0.5, 0.6, 0.7 })
        {
            ReportGate($"confidence >= {threshold:0.00}", decisions.Where(d => d.Confidence >= threshold).ToList());
        }
        foreach (double margin in new[] { 0.05, 0.15, 0.30 })
        {
            ReportGate($"beats the model by {margin:0.00}", decisions.Where(d => d.JevProbability - d.ModelProbability >= margin).ToList());
        }
        // The layer only has to be right about the moves the model got wrong; this is what it would do if
        // it were only consulted there (a gate code could apply, since the mistake predicates are local).
        ReportGate("only where model erred", decisions.Where(d => d.ModelErred && d.Confidence >= TypeSafeClient.DefaultMinimumConfidence).ToList());

        _output.WriteLine("");
        _output.WriteLine("where it disagreed (model's space -> Jev's space, and how often):");
        foreach (var group in decisions.Where(d => d.Disagreed)
                     .GroupBy(d => $"{RondelData.GetSlotName(d.ModelSlot)} -> {RondelData.GetSlotName(d.JevSlot)}")
                     .OrderByDescending(g => g.Count()))
        {
            _output.WriteLine($"  {group.Key,-28} {group.Count(),3}   median confidence {group.OrderBy(d => d.Confidence).ElementAt(group.Count() / 2).Confidence:0.00}");
        }

        _output.WriteLine("");
        _output.WriteLine("by stage of the game (a nation wins at 25 power points):");
        foreach (var band in new[] { (Label: "power 0-8", Min: 0, Max: 8), (Label: "power 9-16", Min: 9, Max: 16), (Label: "power 17+", Min: 17, Max: 99) })
        {
            var inBand = decisions.Where(d => d.Power >= band.Min && d.Power <= band.Max).ToList();
            if (inBand.Count == 0) continue;
            _output.WriteLine($"  {band.Label,-12} n={inBand.Count,3}  disagreed {inBand.Count(d => d.Disagreed),3}  "
                            + $"model erred {inBand.Count(d => d.ModelErred),3}  Jev erred {inBand.Count(d => d.JevErred),3}  "
                            + $"median confidence {inBand.OrderBy(d => d.Confidence).ElementAt(inBand.Count / 2).Confidence:0.00}");
        }

        WriteCsv(decisions);
    }

    private void ReportGate(string label, IReadOnlyList<Decision> taken)
    {
        var overrides = taken.Where(d => d.Disagreed).ToList();
        int fixedUp = overrides.Count(d => d.ModelErred && !d.JevErred);
        int broken = overrides.Count(d => !d.ModelErred && d.JevErred);
        _output.WriteLine($"  {label,-22} {overrides.Count,9} {fixedUp,15} {broken,19} {(fixedUp - broken).ToString("+0;-0;0"),5}");
    }

    /// <summary>The raw rows, so a gate can be re-examined later without paying for the answers again.</summary>
    private void WriteCsv(IReadOnlyList<Decision> decisions)
    {
        string path = Path.Combine(Path.GetTempPath(), $"jev-decisions-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        using var writer = new StreamWriter(path);
        writer.WriteLine("nation,from,modelSlot,jevSlot,confidence,modelProbability,jevProbability,modelWasted,jevWasted,modelSkippedTax,jevSkippedTax,modelDearerTwin,jevDearerTwin,options,turn,power");
        foreach (var d in decisions)
        {
            writer.WriteLine(string.Join(",", new object[]
            {
                d.Nation, RondelData.GetSlotName(d.FromSlot), RondelData.GetSlotName(d.ModelSlot), RondelData.GetSlotName(d.JevSlot),
                d.Confidence.ToString("0.0000", CultureInfo.InvariantCulture),
                d.ModelProbability.ToString("0.0000", CultureInfo.InvariantCulture),
                d.JevProbability.ToString("0.0000", CultureInfo.InvariantCulture),
                d.ModelWasted, d.JevWasted, d.ModelSkippedTaxation, d.JevSkippedTaxation,
                d.ModelPaidForTheFartherTwin, d.JevPaidForTheFartherTwin, d.OptionCount, d.Turn, d.Power
            }));
        }
        _output.WriteLine("");
        _output.WriteLine($"rows: {path}");
    }

    private static ApplicationDbContext CreateContext(string databaseName) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(databaseName).Options);

    private static BotService CreateBotService(string databaseName, ShadowJev shadow)
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
            new AggressiveBotStrategy(), new FriendlyBotStrategy(), shadow
        };

        return new BotService(scopeFactory.Object, hub.Object, strategies, NullLogger<BotService>.Instance);
    }
}
