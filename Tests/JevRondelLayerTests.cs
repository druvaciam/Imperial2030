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
/// The Jev layer is a second opinion on a trained model's rondel move, asked over the spaces that model
/// could legally have moved to. It may send the marker somewhere else; it must never leave the bot worse
/// off than the plain model when the answer cannot be had, and it must never see anything a player at the
/// table cannot (AGENTS.md: no bot-identity cheating).
/// </summary>
[Collection(RlTrainingFlagCollection.Name)]
public class JevRondelLayerTests
{
    private const string ApiKey = "test-key";

    /// <summary>The bot type of the test's layered strategy; registered by name, so no model file is involved.</summary>
    private const string TestBotType = "TestJevLayer";

    /// <summary>Answers one canned body to every request, and keeps what was asked.</summary>
    private sealed class StubTransport : HttpMessageHandler
    {
        private readonly Func<string, HttpResponseMessage> _answer;
        public int Requests { get; private set; }
        public string? LastBody { get; private set; }
        public string? LastAuthorization { get; private set; }

        public StubTransport(Func<string, HttpResponseMessage> answer) => _answer = answer;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            LastBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            LastAuthorization = request.Headers.Authorization?.ToString();
            return _answer(LastBody ?? "");
        }
    }

    private static HttpResponseMessage ChoiceAnswerOf(string chosen, double confidence)
    {
        string number = confidence.ToString(CultureInfo.InvariantCulture);
        string json = "{\"model\":\"jev-1.13.0\",\"answers\":{\"" + JevRlBotStrategy.RondelQuestionId + "\":{"
                    + "\"type\":\"choice\",\"choice\":\"" + chosen + "\","
                    + "\"probabilities\":{\"" + chosen + "\":" + number + "},"
                    + "\"confidence\":" + number + "}},\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
    }

    /// <summary>Plays the space the test names, so the layer has a known move to agree or disagree with.</summary>
    private sealed class FixedPickJev : JevRlBotStrategy
    {
        private readonly int _slot;
        public FixedPickJev(int slot, TypeSafeClient? client) : base(TestBotType, client) => _slot = slot;
        public override double ScoreRondelSlot(int slot, Game game, NationState ns, Player controller, int factories, int units)
            => slot == _slot ? 1000 : -1000;
    }

    private static BotService BuildBotService(params IBotStrategy[] strategies)
    {
        var hub = new Mock<IHubContext<Imperial2030.Server.Hubs.GameHub>>();
        var clients = new Mock<IHubClients>();
        hub.Setup(h => h.Clients).Returns(clients.Object);
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(new Mock<IClientProxy>().Object);
        clients.Setup(c => c.All).Returns(new Mock<IClientProxy>().Object);
        return new BotService(new Mock<IServiceScopeFactory>().Object, hub.Object, strategies.ToList(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BotService>.Instance) { SkipDelays = true };
    }

    /// <summary>
    /// The options as they were asked, option name -> description. Read out of the parsed request, because
    /// the wire form escapes the characters the descriptions are full of ("+" arrives as +).
    /// </summary>
    private static Dictionary<string, string> OptionsAsked(StubTransport transport)
    {
        Assert.NotNull(transport.LastBody);
        using var document = System.Text.Json.JsonDocument.Parse(transport.LastBody!);
        return document.RootElement
            .GetProperty("questions").GetProperty(JevRlBotStrategy.RondelQuestionId).GetProperty("criteria")
            .EnumerateObject().ToDictionary(option => option.Name, option => option.Value.GetString() ?? "");
    }

    private static TypeSafeClient Client(StubTransport transport, string? apiKey = ApiKey, bool askOnEveryMove = false)
    {
        var httpClient = new HttpClient(transport);
        return new TypeSafeClient(apiKey, () => httpClient, null, null, askOnEveryMove);
    }

    /// <summary>
    /// A client that asks about every move. The tests below that are about what the question says, rather
    /// than about when it is asked, use this: the layer only asks about questionable moves by default.
    /// </summary>
    private static TypeSafeClient AlwaysAsking(StubTransport transport) => Client(transport, ApiKey, askOnEveryMove: true);

    /// <summary>
    /// India with four unblocked factories and five flags, its marker on a Maneuver space: Taxation is one
    /// space ahead and worth stopping for, and from Taxation the two Production spaces are two spaces away
    /// free and six spaces away paid for. Both measured RL-5 mistakes are available in this position.
    /// </summary>
    private static (Game Game, NationState India, Player Controller) BuildIndiaOnManeuver(string botType)
    {
        var controller = new Player { Id = Guid.NewGuid(), Cash = 6, IsBot = true, BotType = botType, BotName = "Layered" };
        var rival = new Player { Id = Guid.NewGuid(), Cash = 4, IsBot = true, BotType = "Aggressive", BotName = "Opponent" };
        var india = new NationState
        {
            Nation = Nation.India,
            ControllerId = controller.Id,
            RondelPosition = RondelData.ManeuverSlot2,
            Treasury = 3,
            Power = 4
        };
        var game = new Game
        {
            Id = Guid.NewGuid(),
            Name = "Jev layer",
            Status = GameStatus.InProgress,
            CurrentTurnNation = Nation.India,
            Players = new List<Player> { controller, rival },
            NationStates = new List<NationState>
            {
                india,
                new() { Nation = Nation.China, ControllerId = rival.Id, RondelPosition = RondelData.ImportSlot, Treasury = 5, Power = 8 }
            },
            TerritoryStates = new List<TerritoryState>(),
            Units = new List<Unit>(),
            Bonds = new List<Bond> { new() { Nation = Nation.India, Cost = 9, Interest = 2, HolderId = controller.Id } },
            Actions = new List<GameAction>()
        };

        foreach (string city in new[] { "NewDelhi", "Mumbai", "Kolkata", "Chennai" })
        {
            game.TerritoryStates.Add(new TerritoryState { TerritoryId = city, HasFactory = true });
        }
        foreach (string region in new[] { "Afghanistan", "Iran", "NearEast", "Indochina", "Indonesia" })
        {
            game.TerritoryStates.Add(new TerritoryState { TerritoryId = region, Controller = Nation.India });
        }

        return (game, india, controller);
    }

    private sealed record Position(Game Game, NationState India, Player Controller, JevRlBotStrategy Jev, BotService Service)
    {
        public List<RondelCandidate> Candidates => Service.LegalRondelCandidates(Game, India, Controller, Jev);

        public Task<int?> Reconsider(int scoringPick)
            => Jev.ReconsiderRondelSlotAsync(Game, India, Controller, Candidates, scoringPick);
    }

    private static Position Layered(int modelPick, TypeSafeClient? client)
    {
        var (game, india, controller) = BuildIndiaOnManeuver(TestBotType);
        var jev = new FixedPickJev(modelPick, client);
        return new Position(game, india, controller, jev, BuildBotService(jev));
    }

    [Fact]
    public async Task WithNoApiKeyTheModelsOwnMoveStands()
    {
        var transport = new StubTransport(_ => ChoiceAnswerOf("Taxation (1 space ahead)", 0.99));
        var position = Layered(RondelData.ManeuverSlot1, Client(transport, apiKey: null));

        Assert.Null(await position.Reconsider(RondelData.ManeuverSlot1));
        Assert.Equal(RondelData.ManeuverSlot1, await position.Service.ChooseRondelSlotAsync(position.Game, position.India, position.Controller));
        Assert.Equal(0, transport.Requests);
    }

    [Fact]
    public async Task AConfidentAnswerMovesTheMarkerToTheSpaceItNames()
    {
        var transport = new StubTransport(_ => ChoiceAnswerOf("Taxation (1 space ahead)", 0.87));
        // The model walks past a taxation worth stopping for to reach Maneuver: the measured RL-5 mistake.
        var position = Layered(RondelData.ManeuverSlot1, Client(transport));

        int chosen = await position.Service.ChooseRondelSlotAsync(position.Game, position.India, position.Controller);

        Assert.Equal(1, transport.Requests);
        Assert.Equal($"Bearer {ApiKey}", transport.LastAuthorization);
        Assert.Equal(RondelData.TaxationSlot, chosen);
    }

    [Fact]
    public async Task AnUnconfidentAnswerLeavesTheModelsMoveAlone()
    {
        var transport = new StubTransport(_ => ChoiceAnswerOf("Taxation (1 space ahead)", TypeSafeClient.DefaultMinimumConfidence - 0.01));
        var position = Layered(RondelData.ManeuverSlot1, Client(transport));

        int chosen = await position.Service.ChooseRondelSlotAsync(position.Game, position.India, position.Controller);

        Assert.Equal(1, transport.Requests);
        Assert.Equal(RondelData.ManeuverSlot1, chosen);
    }

    [Theory]
    [InlineData("Taxation (4 spaces ahead)")] // the right action, named with a distance that is not the one offered
    [InlineData("none of the above")]
    public async Task AnAnswerThatNamesNoOfferedSpaceLeavesTheModelsMoveAlone(string answer)
    {
        var transport = new StubTransport(_ => ChoiceAnswerOf(answer, 0.99));
        var position = Layered(RondelData.ManeuverSlot1, Client(transport));

        Assert.Null(await position.Reconsider(RondelData.ManeuverSlot1));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ARefusedRequestLeavesTheModelsMoveAlone(HttpStatusCode status)
    {
        var transport = new StubTransport(_ => new HttpResponseMessage(status) { Content = new StringContent("{\"detail\":\"no\"}") });
        var position = Layered(RondelData.ManeuverSlot1, Client(transport));

        int chosen = await position.Service.ChooseRondelSlotAsync(position.Game, position.India, position.Controller);

        Assert.Equal(RondelData.ManeuverSlot1, chosen);
    }

    [Fact]
    public async Task ATransportThatThrowsLeavesTheModelsMoveAlone()
    {
        var transport = new StubTransport(_ => throw new HttpRequestException("no route to host"));
        var position = Layered(RondelData.ImportSlot, Client(transport));

        int chosen = await position.Service.ChooseRondelSlotAsync(position.Game, position.India, position.Controller);

        Assert.Equal(RondelData.ImportSlot, chosen);
    }

    /// <summary>
    /// Training decides through the training server's action mask, and a training run must not wait on an
    /// outside service - so the layer is inference-only.
    /// </summary>
    [Fact]
    public async Task TrainingIsNeverAsked()
    {
        var transport = new StubTransport(_ => ChoiceAnswerOf("Taxation (1 space ahead)", 0.99));
        var position = Layered(RondelData.ManeuverSlot1, Client(transport));

        bool wasTraining = RLBotStrategy.IsTraining;
        RLBotStrategy.IsTraining = true;
        try
        {
            Assert.Null(await position.Reconsider(RondelData.ManeuverSlot1));
        }
        finally
        {
            RLBotStrategy.IsTraining = wasTraining;
        }

        Assert.Equal(0, transport.Requests);
    }

    /// <summary>
    /// Production and Maneuver each sit on two spaces (p.6). The one that costs more buys the same action
    /// for more money, so it is not offered at all - it is the move the live bot was seen making (3M for
    /// the farther Production with the nearer one free).
    /// </summary>
    [Fact]
    public async Task TheMoreExpensiveOfTwoSpacesWithTheSameActionIsNotOffered()
    {
        var transport = new StubTransport(_ => ChoiceAnswerOf("no answer", 0.0));
        var position = Layered(RondelData.ProductionSlot2, Client(transport));
        position.India.RondelPosition = RondelData.TaxationSlot; // Production two spaces ahead free, the other six and paid for
        var candidates = position.Candidates;

        await position.Jev.ReconsiderRondelSlotAsync(position.Game, position.India, position.Controller, candidates, RondelData.ProductionSlot2);

        // Both spaces were legal, so the model's own pick really was the dearer one.
        Assert.Contains(candidates, candidate => candidate.Slot == RondelData.ProductionSlot2 && candidate.MoveCost > 0);
        var options = OptionsAsked(transport);
        Assert.Contains("Production (2 spaces ahead)", options.Keys);
        Assert.DoesNotContain("Production (6 spaces ahead)", options.Keys);
        // ... and the question says so, rather than pretending the policy picked something else.
        Assert.Contains("the same action is available for less", transport.LastBody);
    }

    /// <summary>The question has to carry what each space would actually produce, or there is nothing to judge.</summary>
    [Fact]
    public async Task TheQuestionCarriesWhatEachSpaceWouldProduceNow()
    {
        var transport = new StubTransport(_ => ChoiceAnswerOf("no answer", 0.0));
        var position = Layered(RondelData.ManeuverSlot1, Client(transport));
        var tax = Imperial2030.Server.Helpers.TaxationHelper.PreviewTaxation(position.Game, position.India);
        var investor = Imperial2030.Server.Helpers.InvestorHelper.PreviewInterestPayment(position.Game, position.India, position.Controller);

        await position.Reconsider(RondelData.ManeuverSlot1);

        var options = OptionsAsked(transport);
        Assert.True(tax.ExpectedPowerGain >= TcpTrainingServer.SkippedTaxationPowerThreshold,
                    $"the position should offer a taxation worth stopping for, got {tax.ExpectedPowerGain} power");
        string taxation = options["Taxation (1 space ahead)"];
        Assert.Contains($"power {position.India.Power} + {tax.ExpectedPowerGain}", taxation);
        Assert.Contains($"Tax revenue {tax.TotalTaxRevenue}m", taxation);
        Assert.Contains($"{tax.ExpectedBonus}m of success bonus", taxation);
        Assert.Contains(investor.NetControllerCashDelta >= 0
            ? $"would receive {investor.NetControllerCashDelta}m"
            : $"would pay {-investor.NetControllerCashDelta}m", options["Investor (5 spaces ahead)"]);
        // Every space the marker may legally reach is asked about, each with its own cost (p.6).
        Assert.Equal(position.Candidates.Count, options.Count);
        Assert.Contains($"costs the government {position.Candidates.First(c => c.Slot == RondelData.ImportSlot).MoveCost}m",
                        options["Import (6 spaces ahead)"]);
        Assert.Contains(TypeSafeClient.Model, transport.LastBody);
    }

    /// <summary>
    /// AGENTS.md: a bot reasons from the position, never from who or what is playing the other nations.
    /// Nothing about an opponent's bot type, strategy or model may reach the decider.
    /// </summary>
    [Fact]
    public async Task NothingAboutHowTheOpponentsDecideIsSent()
    {
        var transport = new StubTransport(_ => ChoiceAnswerOf("no answer", 0.0));
        var position = Layered(RondelData.ManeuverSlot1, Client(transport));

        await position.Reconsider(RondelData.ManeuverSlot1);

        foreach (string identity in new[] { "Default", "Aggressive", "Friendly", "Greedy", "Random", "BotType", "onnx", "Opponent", "Layered", TestBotType })
        {
            Assert.DoesNotContain(identity, transport.LastBody!, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// A nation that already has the units it can use must be told what more of them cost: seen live,
    /// Jev moved China to Production at 0.62 confidence with six armies and three fleets already on the
    /// board and nowhere for them to go. Production is a real option there, but the question has to carry
    /// the soldiers' pay (p.12, 1m per unit at every Taxation), what is left of the nation's pieces, and
    /// what a Maneuver could actually take, or there is nothing to weigh the free units against.
    /// </summary>
    [Fact]
    public async Task ANationAlreadyFullOfUnitsIsToldWhatMoreOfThemCost()
    {
        var transport = new StubTransport(_ => ChoiceAnswerOf("no answer", 0.0));
        var (game, india, controller) = BuildIndiaOnManeuver(TestBotType);
        var china = game.NationStates.First(ns => ns.Nation == Nation.China);
        china.ControllerId = controller.Id;
        china.RondelPosition = RondelData.TaxationSlot;
        foreach (string city in new[] { "Beijing", "Shanghai", "Chongqing" })
        {
            game.TerritoryStates.Add(new TerritoryState { TerritoryId = city, HasFactory = true });
        }
        foreach (string home in new[] { "Beijing", "Beijing", "Beijing", "Chongqing", "Chongqing", "Chongqing" })
        {
            game.Units.Add(new Unit { Id = Guid.NewGuid(), Nation = Nation.China, UnitType = UnitType.Army, TerritoryId = home });
        }
        for (int i = 0; i < 3; i++)
        {
            game.Units.Add(new Unit { Id = Guid.NewGuid(), Nation = Nation.China, UnitType = UnitType.Fleet, TerritoryId = "Shanghai" });
        }
        var jev = new FixedPickJev(RondelData.ManeuverSlot1, AlwaysAsking(transport));
        var service = BuildBotService(jev);

        await jev.ReconsiderRondelSlotAsync(game, china, controller, service.LegalRondelCandidates(game, china, controller, jev), RondelData.ManeuverSlot1);

        var options = OptionsAsked(transport);
        string production = options["Production (2 spaces ahead)"];
        Assert.Contains("6 of its 10 armies and 3 of its 6 fleets", production);
        Assert.Contains("cost it 9m a Taxation", production);
        Assert.Contains("does nothing at all until a Maneuver moves it", production);

        // And the alternative says what it would win, in the same currency.
        string maneuver = options["Maneuver (3 spaces ahead)"];
        Assert.Contains($"holds 0 of its {TaxationRules.MaxFlagsPerNation} flags", maneuver);
        Assert.Contains($"worth {TaxationRules.RevenuePerFlag}m of tax revenue", maneuver);
    }

    /// <summary>
    /// What a Maneuver could take: regions a unit stands next to that no flag of this nation covers, that
    /// hold no foreign unit, and that are not home provinces - <see cref="ManeuverEngine"/> places no flag
    /// in one, whoever owns it.
    /// </summary>
    [Fact]
    public async Task OnlyRegionsAFlagCouldActuallyBePlacedInAreCounted()
    {
        var transport = new StubTransport(_ => ChoiceAnswerOf("no answer", 0.0));
        var (game, india, controller) = BuildIndiaOnManeuver(TestBotType);
        // One Indian army in the Near East: three neutral land neighbours, so there is something to count.
        const string Anchor = "NearEast";
        game.Units.Add(new Unit { Id = Guid.NewGuid(), Nation = Nation.India, UnitType = UnitType.Army, TerritoryId = Anchor });
        var jev = new FixedPickJev(RondelData.ManeuverSlot1, Client(transport));
        var service = BuildBotService(jev);

        var neighbours = MapConnectivity.Adjacency[Anchor]
            .Select(id => TerritoryData.AllTerritories.First(t => t.Id == id))
            .Where(t => t.Type != TerritoryType.Sea && t.Nation == null)
            .ToList();
        Assert.NotEmpty(neighbours); // the position has to offer something, or the test proves nothing

        // A foreign army standing in one of them, and an Indian flag already on another, take both out.
        game.Units.Add(new Unit { Id = Guid.NewGuid(), Nation = Nation.China, UnitType = UnitType.Army, TerritoryId = neighbours[0].Id });
        game.TerritoryStates.Add(new TerritoryState { TerritoryId = neighbours[1].Id, Controller = Nation.India });

        await jev.ReconsiderRondelSlotAsync(game, india, controller, service.LegalRondelCandidates(game, india, controller, jev), RondelData.ManeuverSlot1);

        using var document = System.Text.Json.JsonDocument.Parse(transport.LastBody!);
        int reachable = document.RootElement.GetProperty("state").GetProperty("acting_nation")
            .GetProperty("regions_next_to_its_units_it_could_take_a_flag_in").GetInt32();

        Assert.Equal(neighbours.Count - 2, reachable);
    }

    /// <summary>
    /// The layer asks about a move the code can already see is questionable - here the model walks past a
    /// taxation worth stopping for - and says nothing about a move that looks sound. Measured over 84
    /// head-to-head games, second-guessing every move came out level, so the asking is confined to the
    /// moves where an override has something to repair.
    /// </summary>
    [Fact]
    public async Task OnlyAQuestionableMoveIsAskedAbout()
    {
        var transport = new StubTransport(_ => ChoiceAnswerOf("Taxation (1 space ahead)", 0.9));
        var position = Layered(RondelData.ManeuverSlot1, Client(transport));
        var tax = Imperial2030.Server.Helpers.TaxationHelper.PreviewTaxation(position.Game, position.India);
        Assert.True(tax.ExpectedPowerGain >= Imperial2030.Server.Helpers.RondelOutlookHelper.SkippedTaxationPowerThreshold);

        // Walking past that taxation to reach Maneuver is the questionable move.
        Assert.Equal(RondelData.TaxationSlot, await position.Service.ChooseRondelSlotAsync(position.Game, position.India, position.Controller));
        Assert.Equal(1, transport.Requests);

        // Stopping on it is not, and neither is anything else from a position with nothing to repair.
        var sound = Layered(RondelData.TaxationSlot, Client(transport));
        Assert.Equal(RondelData.TaxationSlot, await sound.Service.ChooseRondelSlotAsync(sound.Game, sound.India, sound.Controller));
        Assert.Equal(1, transport.Requests); // still the one call from above
    }

    /// <summary>The same sound move is asked about when the configuration says to ask about everything.</summary>
    [Fact]
    public async Task AskingAboutEveryMoveIsConfigurable()
    {
        var transport = new StubTransport(_ => ChoiceAnswerOf("no answer", 0.0));
        var position = Layered(RondelData.TaxationSlot, AlwaysAsking(transport));

        await position.Service.ChooseRondelSlotAsync(position.Game, position.India, position.Controller);

        Assert.Equal(1, transport.Requests);
    }

    /// <summary>
    /// Nor is there anything to ask when the model already holds the only space whose action can do
    /// anything: every override available would waste the turn.
    /// </summary>
    [Fact]
    public async Task AMoveThatIsTheOnlyWorkingOneIsNotAskedAbout()
    {
        var transport = new StubTransport(_ => ChoiceAnswerOf("no answer", 0.0));
        var (game, india, controller) = BuildIndiaOnManeuver(TestBotType);
        // An empty treasury and no units: Factory, Import, Production and Maneuver can all do nothing,
        // and the Investor pays this government nothing. Taxation is the only space left that works.
        india.Treasury = 0;
        india.RondelPosition = RondelData.ManeuverSlot2;
        foreach (var withFactory in game.TerritoryStates.Where(ts => ts.HasFactory).ToList()) game.TerritoryStates.Remove(withFactory);
        game.Bonds.Clear();
        var jev = new FixedPickJev(RondelData.TaxationSlot, AlwaysAsking(transport));
        var service = BuildBotService(jev);
        var candidates = service.LegalRondelCandidates(game, india, controller, jev);
        var helper = Imperial2030.Server.Helpers.RondelOutlookHelper.IsWastedRondelSpace;

        Assert.Equal(1, candidates.Count(c => !helper(game, india, c.Slot)));
        Assert.Null(await jev.ReconsiderRondelSlotAsync(game, india, controller, candidates, RondelData.TaxationSlot));
        Assert.Equal(0, transport.Requests);
    }

    /// <summary>A strategy that names a space it may not legally move to is ignored, not played.</summary>
    private sealed class DemandsAnIllegalSpace : DefaultBotStrategy
    {
        public const string BotType = "DemandsAnIllegalSpace";
        public override string Name => BotType;
        private readonly int _demanded;
        public DemandsAnIllegalSpace(int demanded) => _demanded = demanded;
        public override Task<int?> ReconsiderRondelSlotAsync(Game game, NationState ns, Player controller, IReadOnlyList<RondelCandidate> candidates, int scoringPick)
            => Task.FromResult<int?>(_demanded);
    }

    [Fact]
    public async Task ASpaceThatIsNotLegalIsRefusedAndTheScoringPickStands()
    {
        var (game, india, controller) = BuildIndiaOnManeuver(DemandsAnIllegalSpace.BotType);
        india.RondelPosition = RondelData.TaxationSlot;
        // Seven spaces clockwise is further than a marker may move (p.6: at most six).
        var service = BuildBotService(new DemandsAnIllegalSpace(RondelData.ManeuverSlot2));

        int chosen = await service.ChooseRondelSlotAsync(game, india, controller);

        Assert.NotEqual(RondelData.ManeuverSlot2, chosen);
    }

    /// <summary>The layered bot types are only offered when there is a key for the layer to use.</summary>
    [Fact]
    public void TheLayeredBotTypesAreOfferedOnlyWithAConfiguredKey()
    {
        string directory = Path.Combine(Path.GetTempPath(), "jev-catalog-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "RL-5.onnx"), "");

            var without = new BotTypeCatalog(logger: null, modelDirectory: directory, typeSafeConfigured: false).Available;
            var with = new BotTypeCatalog(logger: null, modelDirectory: directory, typeSafeConfigured: true).Available;

            Assert.Contains("RL-5", without);
            Assert.DoesNotContain(JevRlBotStrategy.BotTypePrefix + "RL-5", without);
            Assert.Contains(JevRlBotStrategy.BotTypePrefix + "RL-5", with);
            Assert.Equal("RL-5", JevRlBotStrategy.ModelBotType(JevRlBotStrategy.BotTypePrefix + "RL-5"));
            // The heuristics are not offered under the layer, except Default - the weak baseline the
            // advice is measured against.
            Assert.Contains(JevDefaultBotStrategy.BotType, with);
            Assert.All(BotTypeCatalog.BuiltInBotTypes.Where(type => type != "Default"),
                       type => Assert.DoesNotContain(JevRlBotStrategy.BotTypePrefix + type, with));

            // A bot added without a named type never costs API calls by accident.
            var catalog = new BotTypeCatalog(logger: null, modelDirectory: directory, typeSafeConfigured: true);
            Assert.DoesNotContain(JevRlBotStrategy.BotTypePrefix + "RL-5", catalog.RandomlyAssignable);
            Assert.Contains("RL-5", catalog.RandomlyAssignable);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
