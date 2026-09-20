using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Imperial2030.Server.Models;
using Imperial2030.Server.Services;
using Imperial2030.Server.Services.Bots;
using Imperial2030.Server.Services.Bots.Strategies;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Imperial2030.Tests;

/// <summary>
/// Import is a sequence of single placements (p.8: up to three units at 1M each), and training asks the
/// RL policy about each one on the board as it then is - the unit just placed on it, the treasury 1M
/// lighter. The live bot must ask the same way: place, then ask again, so a strategy that decides
/// "one more?" from the board sees the same board it was trained on.
/// </summary>
public class BotImportStepwiseTests
{
    /// <summary>Imports an army in the first home city as long as the treasury allows, recording what it saw each time it was asked.</summary>
    private sealed class RecordingStrategy : DefaultBotStrategy
    {
        public override string Name => "Recording";
        public readonly List<(int Treasury, int Units)> Seen = new();
        public override (UnitType Type, string TerritoryId)? ChooseNextImport(Game game, NationState ns, int remaining, List<Territory> homeTerritories)
        {
            Seen.Add((ns.Treasury, game.Units.Count(u => u.Nation == ns.Nation)));
            return (UnitType.Army, homeTerritories.OrderBy(t => t.Id).First().Id);
        }
    }

    [Fact]
    public async Task EachImportDecisionSeesTheUnitsAlreadyPlacedAndTheMoneyAlreadySpent()
    {
        var strategy = new RecordingStrategy();
        var bot = new Player { Id = Guid.NewGuid(), IsBot = true, BotType = "Recording", BotName = "Bot" };
        var russia = new NationState { Nation = Nation.Russia, ControllerId = bot.Id, RondelPosition = RondelData.ImportSlot, HasMovedThisTurn = true, Treasury = 5 };
        var game = new Game
        {
            Id = Guid.NewGuid(), Name = "Import", Status = GameStatus.InProgress, CurrentTurnNation = Nation.Russia,
            Players = new List<Player> { bot }, NationStates = new List<NationState> { russia },
            Units = new List<Unit>(), TerritoryStates = TerritoryData.AllTerritories.Select(t => new TerritoryState { TerritoryId = t.Id }).ToList(),
            Bonds = new List<Bond>(), Actions = new List<GameAction>()
        };
        var hub = new Mock<IHubContext<Imperial2030.Server.Hubs.GameHub>>();
        var clients = new Mock<IHubClients>();
        hub.Setup(h => h.Clients).Returns(clients.Object);
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(new Mock<IClientProxy>().Object);
        var service = new BotService(new Mock<IServiceScopeFactory>().Object, hub.Object, new List<IBotStrategy> { strategy, new DefaultBotStrategy() },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BotService>.Instance) { SkipDelays = true };

        await service.BotImport(null, game, russia);

        Assert.Equal(new[] { (5, 0), (4, 1), (3, 2) }, strategy.Seen);
        Assert.Equal(3, game.Units.Count);
        Assert.Equal(2, russia.Treasury);
        Assert.True(russia.HasImportedThisTurn);
        var entry = Assert.Single(game.Actions, a => a.ActionType == "Import");
        Assert.Contains("\"ImportedCount\":3", entry.Metadata);
    }
}
