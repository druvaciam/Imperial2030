using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
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
using Xunit.Abstractions;

namespace Imperial2030.Tests;

/// <summary>
/// One real call to the TypeSafe endpoint, so the question this layer builds can be checked against the
/// live service rather than only against a stub: that the payload is accepted, what Jev answers, and how
/// long it takes. Skipped unless a key is in the environment, because it needs the network and spends
/// quota - the rest of the layer is covered by <see cref="JevRondelLayerTests"/> without either:
///
///   $env:TypeSafe__ApiKey = "<key>"; dotnet test --filter "FullyQualifiedName~JevLiveCallTests"
/// </summary>
public class JevLiveCallTests
{
    private readonly ITestOutputHelper _output;
    public JevLiveCallTests(ITestOutputHelper output) => _output = output;

    private const string ApiKeyEnvironmentVariable = "TypeSafe__ApiKey";

    private sealed class FixedPickJev : JevRlBotStrategy
    {
        private readonly int _slot;
        public FixedPickJev(int slot, TypeSafeClient client) : base("LiveCall", client) => _slot = slot;
        public override double ScoreRondelSlot(int slot, Game game, NationState ns, Player controller, int factories, int units)
            => slot == _slot ? 1000 : -1000;
    }

    /// <summary>
    /// India one space short of a 6-power Taxation with its marker on Maneuver, and a model that moves to
    /// Maneuver anyway - the move measured on RL-5 in 25 of 62 chances. Whatever Jev answers must be a
    /// space the marker could legally reach.
    /// </summary>
    [Fact]
    public async Task JevAnswersTheRondelQuestionWithALegalSpace()
    {
        string? key = Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(key))
        {
            // Nothing to call with, so there is nothing to assert. Says so rather than failing a CI run
            // that has no key: the stubbed tests are the ones that must always run.
            _output.WriteLine($"{ApiKeyEnvironmentVariable} is not set - no call was made.");
            return;
        }

        var controller = new Player { Id = Guid.NewGuid(), Cash = 6, IsBot = true, BotType = "LiveCall", BotName = "Layered" };
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
            Name = "Jev live call",
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

        var hub = new Mock<IHubContext<Imperial2030.Server.Hubs.GameHub>>();
        var clients = new Mock<IHubClients>();
        hub.Setup(h => h.Clients).Returns(clients.Object);
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(new Mock<IClientProxy>().Object);
        clients.Setup(c => c.All).Returns(new Mock<IClientProxy>().Object);

        using var httpClient = new HttpClient();
        var jev = new FixedPickJev(RondelData.ManeuverSlot1, new TypeSafeClient(key, () => httpClient));
        var service = new BotService(new Mock<IServiceScopeFactory>().Object, hub.Object, new IBotStrategy[] { jev },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BotService>.Instance) { SkipDelays = true };

        var candidates = service.LegalRondelCandidates(game, india, controller, jev);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        int chosen = await service.ChooseRondelSlotAsync(game, india, controller);
        stopwatch.Stop();

        _output.WriteLine("offered: " + string.Join(", ", candidates.Select(JevAdviser.OptionName)));
        _output.WriteLine($"the model moved to Maneuver; with the layer: {RondelData.GetSlotName(chosen)} ({stopwatch.ElapsedMilliseconds} ms)");
        Assert.Contains(candidates, candidate => candidate.Slot == chosen);
    }
}
