using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Imperial2030.Server.Models;
using Imperial2030.Server.Services;
using Imperial2030.Server.Services.Bots;
using Imperial2030.Server.Services.Bots.Strategies;
using Imperial2030.Server.Services.TypeSafe;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Imperial2030.Tests;

/// <summary>
/// The layer covers every decision a bot makes, not only the rondel move: which bond to buy, which city
/// takes a factory, which unit to import and where, where each unit moves, whether it stands upright,
/// whether to fight a battle, and whether to pull a factory down.
///
/// Each follows the same contract, and these tests hold it to it: the options come from the shared bot
/// path, an answer naming something that was not offered is refused, and an answer that cannot be had -
/// no key, a refusal, a coin-flip probability - leaves the decision the bot had already made standing.
/// </summary>
[Collection(RlTrainingFlagCollection.Name)]
public class JevDecisionLayerTests
{
    private const string ApiKey = "test-key";
    private const string TestBotType = "TestJevDecisions";

    private sealed class StubTransport : HttpMessageHandler
    {
        private readonly Func<string, HttpResponseMessage> _answer;
        public int Requests { get; private set; }
        public string? LastBody { get; private set; }

        public StubTransport(Func<string, HttpResponseMessage> answer) => _answer = answer;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            LastBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return _answer(LastBody ?? "");
        }
    }

    private static HttpResponseMessage Choice(string questionId, string chosen, double confidence)
    {
        string number = confidence.ToString(CultureInfo.InvariantCulture);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"model\":\"jev-1.13.0\",\"answers\":{\"" + questionId + "\":{"
                + "\"type\":\"choice\",\"choice\":" + System.Text.Json.JsonSerializer.Serialize(chosen) + ","
                + "\"probabilities\":{},\"confidence\":" + number + "}}}")
        };
    }

    private static HttpResponseMessage Noul(string questionId, double probability)
    {
        string number = probability.ToString(CultureInfo.InvariantCulture);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"model\":\"jev-1.13.0\",\"answers\":{\"" + questionId + "\":{"
                + "\"type\":\"noul\",\"noul\":" + number + "}}}")
        };
    }

    private sealed class TestJev : JevRlBotStrategy
    {
        public TestJev(TypeSafeClient client) : base(TestBotType, "RL-TestModel", client) { }
    }

    /// <summary>
    /// Every decision switched on - the default set leaves two of them off on measured grounds (see
    /// TypeSafeClient.DefaultDecisions), and these tests are about the contract, not about that choice.
    /// </summary>
    private static TypeSafeClient Client(StubTransport transport, string? apiKey = ApiKey)
    {
        var httpClient = new HttpClient(transport);
        return new TypeSafeClient(apiKey, () => httpClient, null, null, null, decisions: "all");
    }

    private static Dictionary<string, string> OptionsAsked(StubTransport transport, string questionId)
    {
        Assert.NotNull(transport.LastBody);
        using var document = System.Text.Json.JsonDocument.Parse(transport.LastBody!);
        return document.RootElement.GetProperty("questions").GetProperty(questionId).GetProperty("criteria")
            .EnumerateObject().ToDictionary(option => option.Name, option => option.Value.GetString() ?? "");
    }

    /// <summary>India, four home cities, one factory, an army at home and a fleet in Mumbai's harbour.</summary>
    private static (Game Game, NationState India, Player Controller) Position()
    {
        var controller = new Player { Id = Guid.NewGuid(), Cash = 12, IsBot = true, BotType = TestBotType, BotName = "Layered" };
        var rival = new Player { Id = Guid.NewGuid(), Cash = 9, IsBot = true, BotType = "Aggressive", BotName = "Opponent" };
        var india = new NationState { Nation = Nation.India, ControllerId = controller.Id, RondelPosition = RondelData.ImportSlot, Treasury = 8, Power = 7 };
        var game = new Game
        {
            Id = Guid.NewGuid(),
            Name = "Jev decisions",
            Status = GameStatus.InProgress,
            CurrentTurnNation = Nation.India,
            Players = new List<Player> { controller, rival },
            NationStates = new List<NationState>
            {
                india,
                new() { Nation = Nation.China, ControllerId = rival.Id, RondelPosition = RondelData.TaxationSlot, Treasury = 6, Power = 11 }
            },
            TerritoryStates = new List<TerritoryState> { new() { TerritoryId = "NewDelhi", HasFactory = true } },
            Units = new List<Unit>
            {
                new() { Id = Guid.NewGuid(), Nation = Nation.India, UnitType = UnitType.Army, TerritoryId = "NewDelhi" },
                new() { Id = Guid.NewGuid(), Nation = Nation.India, UnitType = UnitType.Fleet, TerritoryId = "Mumbai" }
            },
            Bonds = new List<Bond>
            {
                new() { Id = Guid.NewGuid(), Nation = Nation.India, Cost = 9, Interest = 2, HolderId = controller.Id },
                new() { Id = Guid.NewGuid(), Nation = Nation.China, Cost = 6, Interest = 1 },   // in the bank, affordable
                new() { Id = Guid.NewGuid(), Nation = Nation.China, Cost = 12, Interest = 3 },  // in the bank, affordable
                new() { Id = Guid.NewGuid(), Nation = Nation.Brazil, Cost = 30, Interest = 7 }  // in the bank, far too dear
            },
            Actions = new List<GameAction>()
        };
        return (game, india, controller);
    }

    // --- Investing ----------------------------------------------------------------------------------

    [Fact]
    public async Task TheBondItNamesIsBought()
    {
        var transport = new StubTransport(_ => Choice(JevRlBotStrategy.BondQuestionId, "China 12m bond, 3 interest", 0.8));
        var (game, _, controller) = Position();
        var jev = new TestJev(Client(transport));
        var affordable = game.Bonds.Where(b => b.HolderId == null && b.Cost <= controller.Cash).ToList();
        var cheap = affordable.Single(b => b.Cost == 6);

        var chosen = await jev.ReconsiderBondAsync(game, controller, affordable, cheap);

        Assert.Equal(12, chosen!.Cost);
        var options = OptionsAsked(transport, JevRlBotStrategy.BondQuestionId);
        Assert.Contains("buy nothing this time", options.Keys);
        Assert.Contains("China 6m bond, 1 interest", options.Keys);
        Assert.DoesNotContain(options.Keys, key => key.StartsWith("Brazil")); // 30m with 12m in hand
        // The description has to carry what the bond is actually worth here, not just its face value.
        Assert.Contains("scores 3 times that factor", options["China 12m bond, 3 interest"]);
    }

    [Fact]
    public async Task AnAnswerOfBuyingNothingKeepsWhateverTheModelDecided()
    {
        var transport = new StubTransport(_ => Choice(JevRlBotStrategy.BondQuestionId, "buy nothing this time", 0.9));
        var (game, _, controller) = Position();
        var jev = new TestJev(Client(transport));
        var affordable = game.Bonds.Where(b => b.HolderId == null && b.Cost <= controller.Cash).ToList();

        Assert.Null(await jev.ReconsiderBondAsync(game, controller, affordable, pick: null));
    }

    /// <summary>
    /// A decision can come before the nation has a marker on the rondel at all - an investor turn on the
    /// first round. Reading the marker as if it were always there threw inside the bot turn, which
    /// stopped the game dead: three games in a 40-game run were lost to it before it was caught.
    /// </summary>
    [Fact]
    public async Task ADecisionBeforeTheMarkerIsOnTheRondelStillWorks()
    {
        var transport = new StubTransport(_ => Choice(JevRlBotStrategy.BondQuestionId, "China 6m bond, 1 interest", 0.8));
        var (game, india, controller) = Position();
        foreach (var ns in game.NationStates) ns.RondelPosition = null;
        var jev = new TestJev(Client(transport));
        var affordable = game.Bonds.Where(b => b.HolderId == null && b.Cost <= controller.Cash).ToList();

        var chosen = await jev.ReconsiderBondAsync(game, controller, affordable, null);

        Assert.Equal(6, chosen!.Cost);
        Assert.Contains("not on the rondel yet", transport.LastBody);
    }

    // --- Building a factory -------------------------------------------------------------------------

    [Fact]
    public async Task TheFactoryGoesToTheCityItNames()
    {
        var transport = new StubTransport(_ => Choice(JevRlBotStrategy.FactoryCityQuestionId, "Mumbai", 0.77));
        var (game, india, controller) = Position();
        var jev = new TestJev(Client(transport));
        var buildable = Imperial2030.Server.Engine.FactoryEngine.BuildableCities(game, Nation.India);

        var chosen = await jev.ReconsiderFactoryCityAsync(game, india, controller, buildable, "Kolkata");

        Assert.Equal("Mumbai", chosen);
        var options = OptionsAsked(transport, JevRlBotStrategy.FactoryCityQuestionId);
        Assert.Equal(buildable.Select(c => c.Id).OrderBy(x => x), options.Keys.OrderBy(x => x));
        Assert.DoesNotContain("NewDelhi", options.Keys); // already has one
        Assert.Contains("shipyard", options["Mumbai"]);  // light blue city
    }

    // --- Importing ----------------------------------------------------------------------------------

    [Fact]
    public async Task TheUnitItNamesIsTheOneImported()
    {
        var transport = new StubTransport(_ => Choice(JevRlBotStrategy.ImportQuestionId, "Fleet in Kolkata", 0.72));
        var (game, india, controller) = Position();
        var jev = new TestJev(Client(transport));
        var placements = TerritoryData.AllTerritories.Where(t => t.Nation == Nation.India)
            .SelectMany(t => new[] { UnitType.Army, UnitType.Fleet }, (t, type) => (Type: type, TerritoryId: t.Id))
            .Where(option => Imperial2030.Server.Engine.ImportEngine.CanPlace(game, Nation.India, option.TerritoryId, option.Type))
            .ToList();

        var chosen = await jev.ReconsiderImportAsync(game, india, controller, placements, (UnitType.Army, "NewDelhi"), remaining: 3);

        Assert.Equal((UnitType.Fleet, "Kolkata"), chosen);
        var options = OptionsAsked(transport, JevRlBotStrategy.ImportQuestionId);
        Assert.Equal(placements.Count, options.Count);
        Assert.DoesNotContain("Fleet in Chennai", options.Keys); // a brown city builds no fleets
    }

    // --- Maneuvering --------------------------------------------------------------------------------

    [Fact]
    public async Task TheUnitGoesWhereItSays()
    {
        var transport = new StubTransport(_ => Choice(JevRlBotStrategy.ManeuverQuestionId, "Afghanistan", 0.81));
        var (game, _, controller) = Position();
        var jev = new TestJev(Client(transport));
        var army = game.Units.First(u => u.UnitType == UnitType.Army);
        var destinations = new List<string> { "NewDelhi", "Kolkata", "Afghanistan", "Urumqi" };

        var chosen = await jev.ReconsiderManeuverDestinationAsync(game, army, controller, destinations, "Kolkata");

        Assert.Equal("Afghanistan", chosen);
        Assert.Contains("Stay in", OptionsAsked(transport, JevRlBotStrategy.ManeuverQuestionId)["NewDelhi"]);
    }

    [Fact]
    public async Task ADestinationItCouldNotReachIsRefused()
    {
        var transport = new StubTransport(_ => Choice(JevRlBotStrategy.ManeuverQuestionId, "Berlin", 0.99));
        var (game, _, controller) = Position();
        var jev = new TestJev(Client(transport));
        var army = game.Units.First(u => u.UnitType == UnitType.Army);

        Assert.Null(await jev.ReconsiderManeuverDestinationAsync(game, army, controller,
            new List<string> { "NewDelhi", "Kolkata", "Afghanistan" }, "Kolkata"));
    }

    // --- The yes/no decisions -----------------------------------------------------------------------

    [Theory]
    [InlineData(0.95, true)]   // clearly yes
    [InlineData(0.04, false)]  // clearly no
    public async Task AClearYesOrNoDecidesThePosture(double probability, bool expected)
    {
        var transport = new StubTransport(_ => Noul(JevRlBotStrategy.HostilityQuestionId, probability));
        var (game, _, controller) = Position();
        var jev = new TestJev(Client(transport));
        var army = game.Units.First(u => u.UnitType == UnitType.Army);
        // A foreign army in the province: standing upright there offers it a battle, so there is something to decide.
        game.Units.Add(new Unit { Id = Guid.NewGuid(), Nation = Nation.China, UnitType = UnitType.Army, TerritoryId = "Urumqi" });

        Assert.Equal(expected, await jev.ReconsiderHostilityAsync(game, army, controller, "Urumqi", pick: !expected));
    }

    /// <summary>
    /// A Noul near 0.5 means the two answers are about equally likely, not a weak yes - so it decides
    /// nothing and the bot keeps its own posture.
    /// </summary>
    [Theory]
    [InlineData(0.55)]
    [InlineData(0.40)]
    public async Task ACoinFlipDecidesNothing(double probability)
    {
        var transport = new StubTransport(_ => Noul(JevRlBotStrategy.HostilityQuestionId, probability));
        var (game, _, controller) = Position();
        var jev = new TestJev(Client(transport));
        var army = game.Units.First(u => u.UnitType == UnitType.Army);
        game.Units.Add(new Unit { Id = Guid.NewGuid(), Nation = Nation.China, UnitType = UnitType.Army, TerritoryId = "Urumqi" });

        Assert.Null(await jev.ReconsiderHostilityAsync(game, army, controller, "Urumqi", pick: true));
        Assert.Equal(1, transport.Requests);
    }

    [Fact]
    public async Task TheBattleAnswerIsTaken()
    {
        var transport = new StubTransport(_ => Noul(JevRlBotStrategy.BattleQuestionId, 0.9));
        var (game, _, controller) = Position();
        var jev = new TestJev(Client(transport));
        var battle = new PendingBattle
        {
            TerritoryId = "Afghanistan",
            AggressorNation = Nation.China,
            DefenderNations = new List<Nation> { Nation.India }
        };

        Assert.True(await jev.ReconsiderBattleResponseAsync(game, battle, controller, fightPick: false));
    }

    [Fact]
    public async Task TheFactoryDestructionAnswerIsTaken()
    {
        var transport = new StubTransport(_ => Noul(JevRlBotStrategy.FactoryDestructionQuestionId, 0.02));
        var (game, _, controller) = Position();
        var jev = new TestJev(Client(transport));

        Assert.False(await jev.ReconsiderFactoryDestructionAsync(game, Nation.India, "Beijing", controller, destroyPick: true));
    }

    /// <summary>
    /// A unit whose destinations all amount to the same thing is not worth a call: the question is asked
    /// where the choices differ in what they would do. Every destination stays on the table either way -
    /// this decides what to spend a call on, not what the unit may do.
    /// </summary>
    [Fact]
    public async Task AUnitWithNothingToWeighIsNotAskedAbout()
    {
        var transport = new StubTransport(_ => Choice(JevRlBotStrategy.ManeuverQuestionId, "Kolkata", 0.99));
        var (game, _, controller) = Position();
        var jev = new TestJev(Client(transport));
        var army = game.Units.First(u => u.UnitType == UnitType.Army);

        // Two of its own home provinces, empty: the same nothing either way.
        Assert.Null(await jev.ReconsiderManeuverDestinationAsync(game, army, controller,
            new[] { "NewDelhi", "Kolkata" }, "NewDelhi"));
        Assert.Equal(0, transport.Requests);

        // Add a neutral region it could take a flag in, and the choice is worth asking about.
        Assert.NotNull(await jev.ReconsiderManeuverDestinationAsync(game, army, controller,
            new[] { "NewDelhi", "Kolkata", "Afghanistan" }, "NewDelhi"));
        Assert.Equal(1, transport.Requests);
    }

    /// <summary>
    /// Standing upright decides something where it offers a battle or shuts a factory down. In an empty
    /// province with neither, it decides nothing worth a call.
    /// </summary>
    [Fact]
    public async Task PostureIsOnlyAskedAboutWhereItDecidesSomething()
    {
        var transport = new StubTransport(_ => Noul(JevRlBotStrategy.HostilityQuestionId, 0.95));
        var (game, _, controller) = Position();
        var jev = new TestJev(Client(transport));
        var army = game.Units.First(u => u.UnitType == UnitType.Army);

        Assert.Null(await jev.ReconsiderHostilityAsync(game, army, controller, "Afghanistan", pick: false));
        Assert.Equal(0, transport.Requests);

        // A foreign unit in the region makes it a question about a battle.
        game.Units.Add(new Unit { Id = Guid.NewGuid(), Nation = Nation.China, UnitType = UnitType.Army, TerritoryId = "Afghanistan" });
        Assert.True(await jev.ReconsiderHostilityAsync(game, army, controller, "Afghanistan", pick: false));
        Assert.Equal(1, transport.Requests);
    }

    // --- The guarantees, for every decision ----------------------------------------------------------

    [Fact]
    public async Task WithNoKeyNothingIsAskedAndNothingChanges()
    {
        var transport = new StubTransport(_ => Choice(JevRlBotStrategy.BondQuestionId, "China 12m bond, 3 interest", 0.99));
        var (game, india, controller) = Position();
        var jev = new TestJev(Client(transport, apiKey: null));
        var army = game.Units.First(u => u.UnitType == UnitType.Army);
        var affordable = game.Bonds.Where(b => b.HolderId == null && b.Cost <= controller.Cash).ToList();

        Assert.Null(await jev.ReconsiderBondAsync(game, controller, affordable, affordable[0]));
        Assert.Null(await jev.ReconsiderFactoryCityAsync(game, india, controller,
            Imperial2030.Server.Engine.FactoryEngine.BuildableCities(game, Nation.India), "Mumbai"));
        Assert.Null(await jev.ReconsiderImportAsync(game, india, controller,
            new[] { (UnitType.Army, "NewDelhi"), (UnitType.Fleet, "Mumbai") }, (UnitType.Army, "NewDelhi"), 3));
        Assert.Null(await jev.ReconsiderManeuverDestinationAsync(game, army, controller, new[] { "NewDelhi", "Afghanistan" }, "NewDelhi"));
        Assert.Null(await jev.ReconsiderHostilityAsync(game, army, controller, "Urumqi", true));
        Assert.Null(await jev.ReconsiderFactoryDestructionAsync(game, Nation.India, "Beijing", controller, true));
        Assert.Equal(0, transport.Requests);
    }

    [Fact]
    public async Task ARefusedRequestLeavesEveryDecisionAsItWas()
    {
        var transport = new StubTransport(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") });
        var (game, india, controller) = Position();
        var jev = new TestJev(Client(transport));
        var army = game.Units.First(u => u.UnitType == UnitType.Army);
        var affordable = game.Bonds.Where(b => b.HolderId == null && b.Cost <= controller.Cash).ToList();

        game.Units.Add(new Unit { Id = Guid.NewGuid(), Nation = Nation.China, UnitType = UnitType.Army, TerritoryId = "Urumqi" });

        Assert.Null(await jev.ReconsiderBondAsync(game, controller, affordable, affordable[0]));
        Assert.Null(await jev.ReconsiderManeuverDestinationAsync(game, army, controller, new[] { "NewDelhi", "Afghanistan" }, "NewDelhi"));
        Assert.Null(await jev.ReconsiderHostilityAsync(game, army, controller, "Urumqi", true));
        Assert.Equal(3, transport.Requests);
    }

    /// <summary>Training decides through the action mask and must never wait on an outside service.</summary>
    [Fact]
    public async Task TrainingIsNeverAskedAboutAnyDecision()
    {
        var transport = new StubTransport(_ => Choice(JevRlBotStrategy.BondQuestionId, "China 12m bond, 3 interest", 0.99));
        var (game, india, controller) = Position();
        var jev = new TestJev(Client(transport));
        var army = game.Units.First(u => u.UnitType == UnitType.Army);
        var affordable = game.Bonds.Where(b => b.HolderId == null && b.Cost <= controller.Cash).ToList();

        bool wasTraining = RLBotStrategy.IsTraining;
        RLBotStrategy.IsTraining = true;
        try
        {
            Assert.Null(await jev.ReconsiderBondAsync(game, controller, affordable, affordable[0]));
            Assert.Null(await jev.ReconsiderImportAsync(game, india, controller,
                new[] { (UnitType.Army, "NewDelhi"), (UnitType.Fleet, "Mumbai") }, (UnitType.Army, "NewDelhi"), 3));
            Assert.Null(await jev.ReconsiderManeuverDestinationAsync(game, army, controller, new[] { "NewDelhi", "Afghanistan" }, "NewDelhi"));
            Assert.Null(await jev.ReconsiderHostilityAsync(game, army, controller, "Urumqi", true));
        }
        finally
        {
            RLBotStrategy.IsTraining = wasTraining;
        }

        Assert.Equal(0, transport.Requests);
    }

    /// <summary>
    /// The decisions asked about by default are the ones that measured level or better. The two left out
    /// - where each unit moves and whether it stands upright - lost 13 points a game over 52 games, and
    /// were four fifths of the calls. They are still implemented, and "all" switches them back on.
    /// </summary>
    [Fact]
    public async Task ByDefaultTheTwoDecisionsThatMeasuredWorseAreLeftToTheModel()
    {
        var transport = new StubTransport(_ => Choice(JevRlBotStrategy.ManeuverQuestionId, "Afghanistan", 0.99));
        var (game, _, controller) = Position();
        var httpClient = new HttpClient(transport);
        var jev = new TestJev(new TypeSafeClient(ApiKey, () => httpClient)); // no decision list: the default set
        var army = game.Units.First(u => u.UnitType == UnitType.Army);
        game.Units.Add(new Unit { Id = Guid.NewGuid(), Nation = Nation.China, UnitType = UnitType.Army, TerritoryId = "Urumqi" });

        Assert.Null(await jev.ReconsiderManeuverDestinationAsync(game, army, controller, new[] { "NewDelhi", "Kolkata", "Afghanistan" }, "Kolkata"));
        Assert.Null(await jev.ReconsiderHostilityAsync(game, army, controller, "Urumqi", pick: false));
        Assert.Equal(0, transport.Requests);

        Assert.DoesNotContain(JevRlBotStrategy.ManeuverQuestionId, TypeSafeClient.DefaultDecisions);
        Assert.DoesNotContain(JevRlBotStrategy.HostilityQuestionId, TypeSafeClient.DefaultDecisions);
        foreach (string asked in new[] { JevRlBotStrategy.RondelQuestionId, JevRlBotStrategy.BondQuestionId,
                                         JevRlBotStrategy.FactoryCityQuestionId, JevRlBotStrategy.ImportQuestionId,
                                         JevRlBotStrategy.BattleQuestionId, JevRlBotStrategy.FactoryDestructionQuestionId })
        {
            Assert.Contains(asked, TypeSafeClient.DefaultDecisions);
        }
    }

    /// <summary>
    /// AGENTS.md: a bot reasons from the position, never from who or what plays the other nations. The
    /// same boundary the rondel question keeps, kept by every other question too.
    /// </summary>
    [Fact]
    public async Task NoQuestionCarriesAnythingAboutHowTheOpponentsDecide()
    {
        var bodies = new List<string>();
        var transport = new StubTransport(body => { bodies.Add(body); return Choice("x", "y", 0.0); });
        var (game, india, controller) = Position();
        var jev = new TestJev(Client(transport));
        var army = game.Units.First(u => u.UnitType == UnitType.Army);
        var affordable = game.Bonds.Where(b => b.HolderId == null && b.Cost <= controller.Cash).ToList();

        await jev.ReconsiderBondAsync(game, controller, affordable, affordable[0]);
        await jev.ReconsiderFactoryCityAsync(game, india, controller,
            Imperial2030.Server.Engine.FactoryEngine.BuildableCities(game, Nation.India), "Mumbai");
        await jev.ReconsiderManeuverDestinationAsync(game, army, controller, new[] { "NewDelhi", "Kolkata", "Afghanistan" }, "Kolkata");
        game.Units.Add(new Unit { Id = Guid.NewGuid(), Nation = Nation.China, UnitType = UnitType.Army, TerritoryId = "Urumqi" });
        await jev.ReconsiderHostilityAsync(game, army, controller, "Urumqi", true);

        Assert.Equal(4, bodies.Count);
        foreach (string body in bodies)
        {
            foreach (string identity in new[] { "Aggressive", "Default", "Greedy", "Friendly", "BotType", "onnx", "Opponent", "Layered", TestBotType })
            {
                Assert.DoesNotContain(identity, body, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
