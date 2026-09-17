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
/// A bot that takes over a nation MID-TURN must not repeat the rondel action that was already taken.
///
/// BotService.ExecuteBotTurn re-enters a turn with HasMovedThisTurn set, skips the move, and runs the
/// slot's action - which may already have been completed. (A government change in the Investor turn
/// of a pass-over used to do this; in the p.11 order that Investor turn follows the action and the
/// rotation moves on with it, so the re-entry with a new player no longer arises - the guard stays.)
/// The bot must recognise a completed action and simply end the turn; repeating it would be a second
/// import, factory or production in one turn (p.7: one action per turn).
/// </summary>
public class BotRondelActionReentryTests
{
    private static BotService BuildBotService()
    {
        var hub = new Mock<IHubContext<Imperial2030.Server.Hubs.GameHub>>();
        var clients = new Mock<IHubClients>();
        hub.Setup(h => h.Clients).Returns(clients.Object);
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(new Mock<IClientProxy>().Object);
        clients.Setup(c => c.All).Returns(new Mock<IClientProxy>().Object);

        return new BotService(
            new Mock<IServiceScopeFactory>().Object,
            hub.Object,
            new List<IBotStrategy> { new DefaultBotStrategy(), new RandomBotStrategy() },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BotService>.Instance)
        { SkipDelays = true };
    }

    private static (Game Game, NationState Ns, Player Bot) BuildBoard(int slot)
    {
        var bot = new Player { Id = Guid.NewGuid(), IsBot = true, BotType = "Random", BotName = "Random Bot 1", Cash = 5 };
        var ns = new NationState { Nation = Nation.China, ControllerId = bot.Id, Treasury = 9, RondelPosition = slot, HasMovedThisTurn = true };
        var game = new Game
        {
            Id = Guid.NewGuid(),
            Name = "Re-entry",
            Status = GameStatus.InProgress,
            CurrentTurnNation = Nation.China,
            Players = new List<Player> { bot },
            NationStates = new List<NationState> { ns },
            Units = new List<Unit>(),
            TerritoryStates = TerritoryData.AllTerritories.Select(t => new TerritoryState { TerritoryId = t.Id }).ToList(),
            Bonds = new List<Bond>(),
            Actions = new List<GameAction>()
        };
        return (game, ns, bot);
    }

    [Fact]
    public async Task AnImportAlreadyTakenThisTurnIsNotRepeated()
    {
        var (game, ns, _) = BuildBoard(RondelData.ImportSlot);
        ns.HasImportedThisTurn = true;

        await BuildBotService().BotImport(null, game, ns);

        Assert.Empty(game.Units);
        Assert.Equal(9, ns.Treasury);
        Assert.Empty(game.Actions);
    }

    [Fact]
    public async Task AFactoryDecisionAlreadyTakenThisTurnIsNotRepeated()
    {
        var (game, ns, bot) = BuildBoard(RondelData.FactorySlot);
        ns.HasBuiltThisTurn = true;

        await BuildBotService().BotBuildFactory(null, game, ns, bot);

        Assert.DoesNotContain(game.TerritoryStates, t => t.HasFactory);
        Assert.Equal(9, ns.Treasury);
        Assert.Empty(game.Actions);
    }

    [Fact]
    public async Task AProductionAlreadyTakenThisTurnIsNotRepeated()
    {
        var (game, ns, _) = BuildBoard(RondelData.ProductionSlot1);
        game.TerritoryStates.First(t => t.TerritoryId == TerritoryData.AllTerritories.First(x => x.IsHomeCity(Nation.China)).Id).HasFactory = true;
        ns.HasProducedThisTurn = true;

        await BuildBotService().BotProduction(null, game, ns);

        Assert.Empty(game.Units);
        Assert.Empty(game.Actions);
    }
}
