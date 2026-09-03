using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Imperial2030.Server.Data;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace Imperial2030.Tests;

/// <summary>
/// Measures an RL bot's rondel-slot distribution SPLIT BY THE NATION IT CONTROLS.
///
/// Motivated by two exported games in which RL-3 played competently as Russia (20% Maneuver, 35%
/// Production) and incompetently as India — 54% Maneuver with zero units in one game, 42% Investor with
/// zero factories in another. An aggregate win rate cannot see that: the bot's average looks reasonable
/// while one nation is played nonsensically.
///
/// Two games is not evidence, which is why this exists. GetOrderedPlayers-style aggregate stats hide
/// per-nation collapse; this prints the same table across many games so the question "does the weakness
/// track the NATION, or is it just variance?" can be answered with an n worth trusting.
///
/// Deliberately a diagnostic, not an assertion about quality: it asserts only that the harness produced
/// usable data. The numbers go to test output for a human to read. Making it fail on, say, "Production
/// must exceed 10%" would turn a measurement into a flaky quality gate.
/// </summary>
public class RLPerNationBehaviourTests
{
    private readonly ITestOutputHelper _output;

    public RLPerNationBehaviourTests(ITestOutputHelper output) => _output = output;

    /// <summary>Kept small so the suite stays fast; raise locally when investigating.</summary>
    private const int GameCount = 12;

    private static readonly TimeSpan HardTimeout = TimeSpan.FromMinutes(8);

    /// <summary>
    /// The number of builds a nation normally has available in a game, used as the denominator of the
    /// built/max column below.
    ///
    /// A nation has four home cities and may hold one factory in each (Imperial-2030-Rules.pdf p.7,
    /// "Only one factory may be built in each city"); two are already built at setup (p.4, "Each nation
    /// starts with two factories"). So two builds, normally.
    ///
    /// NOT a hard ceiling, which is why this is named "typical": three armies can destroy a foreign
    /// factory (p.11) and the freed city can be built in again, so a nation that loses factories can
    /// exceed two. Rare in practice - one destruction across the whole 260-move exported game - so it is
    /// still the right yardstick, just not an upper bound, and a value above 1.0 per stint is not a bug.
    ///
    /// The point of measuring builds at all is that the Factory *percentage* column is close to
    /// meaningless: with roughly two useful landings available per nation-game however many rondel moves
    /// it gets, a slot frequency mostly measures game length. The built/max column is the real number.
    /// </summary>
    private const int TypicalBuildableFactories = 2;

    private static readonly Dictionary<int, string> SlotNames = new()
    {
        [RondelData.TaxationSlot] = "Tax",
        [RondelData.FactorySlot] = "Factory",
        [RondelData.ProductionSlot1] = "Prod",
        [RondelData.ProductionSlot2] = "Prod",
        [RondelData.ManeuverSlot1] = "Maneuver",
        [RondelData.ManeuverSlot2] = "Maneuver",
        [RondelData.InvestorSlot] = "Investor",
        [RondelData.ImportSlot] = "Import",
    };

    private static ApplicationDbContext GetDbContext(string dbName) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(dbName).Options);

    [Theory]
    [InlineData("RL-3")]
    [InlineData("RL-4")]
    public async Task ReportRondelDistributionPerControlledNation(string botType)
    {
        // nation -> slot name -> count, for turns the RL bot actually controlled that nation.
        var perNation = new Dictionary<Nation, Dictionary<string, int>>();
        var nationGames = new Dictionary<Nation, int>();
        // Factories actually BUILT, which is the metric the supply cap makes meaningful (see class docs).
        var factoriesBuilt = new Dictionary<Nation, int>();
        int rlFactories = 0, allFactories = 0, rlStints = 0, allStints = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        int played = 0;

        for (int g = 0; g < GameCount && stopwatch.Elapsed < HardTimeout; g++)
        {
            played++;
            // One store name per game, shared with every scoped context BotService resolves,
            // so the bot's background work and this test see the same data.
            var dbName = Guid.NewGuid().ToString();
            using var context = GetDbContext(dbName);

            var hub = new Mock<IHubContext<Imperial2030.Server.Hubs.GameHub>>();
            var clients = new Mock<IHubClients>();
            hub.Setup(h => h.Clients).Returns(clients.Object);
            clients.Setup(c => c.Group(It.IsAny<string>())).Returns(new Mock<IClientProxy>().Object);
            clients.Setup(c => c.All).Returns(new Mock<IClientProxy>().Object);

            var scopeFactory = new Mock<IServiceScopeFactory>();
            scopeFactory.Setup(s => s.CreateScope()).Returns(() =>
            {
                var scope = new Mock<IServiceScope>();
                var provider = new Mock<IServiceProvider>();
                provider.Setup(p => p.GetService(typeof(ApplicationDbContext)))
                        .Returns(GetDbContext(dbName));
                scope.Setup(s => s.ServiceProvider).Returns(provider.Object);
                return scope.Object;
            });

            var botService = new Imperial2030.Server.Services.BotService(
                scopeFactory.Object, hub.Object,
                new List<Imperial2030.Server.Services.Bots.IBotStrategy>
                {
                    new Imperial2030.Server.Services.Bots.Strategies.DefaultBotStrategy(),
                    new Imperial2030.Server.Services.Bots.Strategies.RandomBotStrategy(),
                    new Imperial2030.Server.Services.Bots.Strategies.GreedyBotStrategy(),
                    new Imperial2030.Server.Services.Bots.Strategies.AggressiveBotStrategy(),
                    new Imperial2030.Server.Services.Bots.Strategies.FriendlyBotStrategy(),
                },
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Imperial2030.Server.Services.BotService>.Instance)
            { SkipDelays = true };

            var userManager = new Mock<UserManager<ApplicationUser>>(
                new Mock<IUserStore<ApplicationUser>>().Object, null, null, null, null, null, null, null, null);

            var controller = new Imperial2030.Server.Controllers.GamesController(
                context, userManager.Object, hub.Object,
                new Imperial2030.Server.Services.PresenceTracker(), botService,
                new Mock<Imperial2030.Server.Services.INotificationService>().Object);

            var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "host-user") }, "Test");
            controller.ControllerContext = new ControllerContext(new ActionContext(
                new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
                new Microsoft.AspNetCore.Routing.RouteData(),
                new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor()));

            var created = await controller.CreateGame(new CreateGameRequest
            {
                Name = $"PerNation_{g}",
                MaxPlayers = 6,
                IsPrivate = false
            });
            var gameId = Assert.IsType<GameDto>(Assert.IsType<CreatedAtActionResult>(created.Result).Value).Id;

            for (int i = 0; i < 5; i++) await controller.AddBot(gameId);

            var players = context.Players.Where(p => p.GameId == gameId).ToList();
            players[0].IsBot = true;
            players[0].BotName = $"{botType} Bot";
            players[0].BotType = botType;

            var opponents = new[] { "Random", "Default", "Greedy", "Aggressive", "Friendly" };
            var rng = new Random(g);
            for (int i = 1; i < 6; i++)
            {
                players[i].IsBot = true;
                players[i].BotType = opponents[rng.Next(opponents.Length)];
                players[i].BotName = $"{players[i].BotType} Bot {i}";
            }
            await context.SaveChangesAsync();

            Assert.IsAssignableFrom<OkResult>(await controller.StartGame(gameId));

            int ticks = 0;
            while (ticks < 2000 && stopwatch.Elapsed < HardTimeout)
            {
                var snapshot = context.Games.AsNoTracking().FirstOrDefault(x => x.Id == gameId);
                if (snapshot == null || snapshot.Status == GameStatus.Finished) break;
                await Task.Delay(20);
                ticks++;
            }

            context.ChangeTracker.Clear();
            var final = context.Games.AsNoTracking()
                .Include(x => x.Players)
                .Include(x => x.Actions)
                .AsSplitQuery()
                .FirstOrDefault(x => x.Id == gameId);
            if (final == null) continue;

            var rlName = final.Players.FirstOrDefault(p => p.BotType == botType)?.BotName;
            if (rlName == null) continue;

            var seenThisGame = new HashSet<Nation>();
            // (player, nation) pairs, so RL's share of factories can be compared against its share of
            // the nation-stints that were available to build them in.
            var stintsThisGame = new HashSet<(string, Nation)>();

            foreach (var action in final.Actions.Where(a => a.Nation.HasValue && a.PlayerName != null))
            {
                var nation = action.Nation!.Value;
                var isRl = action.PlayerName == rlName;

                if (action.ActionType == "Factory")
                {
                    allFactories++;
                    if (isRl)
                    {
                        rlFactories++;
                        factoriesBuilt[nation] = factoriesBuilt.GetValueOrDefault(nation) + 1;
                    }
                    continue;
                }

                if (action.ActionType != "Move" || string.IsNullOrEmpty(action.Metadata)) continue;

                int? slot;
                try
                {
                    slot = JsonSerializer.Deserialize<RondelMoveMetadata>(action.Metadata,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })?.TargetSlot;
                }
                catch (JsonException) { continue; }
                if (slot is null || !SlotNames.TryGetValue(slot.Value, out var name)) continue;

                stintsThisGame.Add((action.PlayerName!, nation));
                if (!isRl) continue;

                if (!perNation.TryGetValue(nation, out var counts))
                {
                    perNation[nation] = counts = new Dictionary<string, int>();
                }
                counts[name] = counts.GetValueOrDefault(name) + 1;
                seenThisGame.Add(nation);
            }

            allStints += stintsThisGame.Count;
            rlStints += stintsThisGame.Count(x => x.Item1 == rlName);

            foreach (var n in seenThisGame)
            {
                nationGames[n] = nationGames.GetValueOrDefault(n) + 1;
            }
        }

        _output.WriteLine($"=== {botType}: rondel distribution per controlled nation ({played} games) ===");
        _output.WriteLine($"{"nation",-8} {"games",5} {"moves",6}  {"Prod",6} {"Maneuver",9} {"Factory",8} {"Tax",6} {"Investor",9} {"Import",7}  {"built/max",10}");

        foreach (var (nation, counts) in perNation.OrderBy(kv => kv.Key))
        {
            int total = counts.Values.Sum();
            if (total == 0) continue;
            string Pct(string k) => $"{100.0 * counts.GetValueOrDefault(k) / total,5:0}%";
            _output.WriteLine(
                $"{nation,-8} {nationGames.GetValueOrDefault(nation),5} {total,6}  " +
                $"{Pct("Prod"),6} {Pct("Maneuver"),9} {Pct("Factory"),8} {Pct("Tax"),6} " +
                $"{Pct("Investor"),9} {Pct("Import"),7}  " +
                $"{factoriesBuilt.GetValueOrDefault(nation)}/{TypicalBuildableFactories * nationGames.GetValueOrDefault(nation),-8}");
        }

        _output.WriteLine("");
        _output.WriteLine(
            $"factories built: {botType} {rlFactories} in {rlStints} nation-stints, " +
            $"everyone else {allFactories - rlFactories} in {allStints - rlStints}. " +
            $"Per stint: {botType} {(rlStints == 0 ? 0 : (double)rlFactories / rlStints):0.00} vs " +
            $"{(allStints == rlStints ? 0 : (double)(allFactories - rlFactories) / (allStints - rlStints)):0.00} " +
            $"(ceiling {TypicalBuildableFactories} per nation per game).");

        // The point is the table above. Assert only that the harness produced data worth reading -
        // turning "Production must be > N%" into a gate would make this a flaky quality test.
        Assert.True(perNation.Count > 0,
            $"No rondel moves were recorded for {botType} across {played} games - the harness produced no data.");
    }
}
