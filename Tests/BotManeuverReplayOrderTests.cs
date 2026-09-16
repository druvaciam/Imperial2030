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
/// A bot that keeps a unit where it is and stands it up must log the toggle BEFORE the stay, with the
/// stay carrying the unit's hostility after the toggle.
///
/// Replay identifies the unit a <c>ToggleHostility</c> entry refers to as "one whose hostility differs
/// from the logged result and that has not moved yet", and the unit a stay entry refers to by its logged
/// <c>SourceIsHostile</c>. When two identical units share a territory, that is the only thing telling the
/// two apart: toggle-then-stay makes each entry pick the same unit again; stay-then-toggle lets the
/// replay stand up the OTHER unit, after which the two boards differ in which unit is hostile - silently,
/// since the logs match - and a hostile unit on a factory is a 2M difference at the next taxation.
/// </summary>
public class BotManeuverReplayOrderTests
{
    /// <summary>Always stays, always stands up. Deterministic where the shipped strategies roll dice.</summary>
    private sealed class StayAndStandStrategy : DefaultBotStrategy
    {
        public override string Name => "StayAndStand";
        public override double ScoreManeuverDestination(Game game, Unit unit, string destinationId, Player controller)
            => destinationId == unit.TerritoryId ? 1000 : 0;
        public override bool DetermineHostility(bool hasEnemy, bool isForeignHome) => true;
    }

    private static BotService BuildBotService()
    {
        var hub = new Mock<IHubContext<Imperial2030.Server.Hubs.GameHub>>();
        var clients = new Mock<IHubClients>();
        hub.Setup(h => h.Clients).Returns(clients.Object);
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(new Mock<IClientProxy>().Object);
        clients.Setup(c => c.All).Returns(new Mock<IClientProxy>().Object);
        return new BotService(new Mock<IServiceScopeFactory>().Object, hub.Object,
            new List<IBotStrategy> { new StayAndStandStrategy(), new DefaultBotStrategy() },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BotService>.Instance) { SkipDelays = true };
    }

    [Fact]
    public async Task AStandingUpStayLogsTheToggleFirstAndTheStayWithThePostToggleHostility()
    {
        var bot = new Player { Id = Guid.NewGuid(), IsBot = true, BotType = "StayAndStand", BotName = "Bot" };
        var russia = new NationState { Nation = Nation.Russia, ControllerId = bot.Id, RondelPosition = RondelData.ManeuverSlot1 };
        // A foreign home province with no factory: standing up there is the bot's choice, and nothing
        // about the p.10 last-factory protection applies.
        var region = TerritoryData.AllTerritories.First(t => t.IsHomeCity(Nation.China)).Id;
        var game = new Game
        {
            Id = Guid.NewGuid(),
            Name = "Replay order",
            Status = GameStatus.InProgress,
            CurrentTurnNation = Nation.Russia,
            CurrentManeuverPhase = ManeuverPhase.Fleets,
            Players = new List<Player> { bot },
            NationStates = new List<NationState> { russia },
            Units = new List<Unit>
            {
                // Two identical armies: the case replay cannot tell apart by anything but the log's own hints.
                new() { Id = Guid.NewGuid(), Nation = Nation.Russia, UnitType = UnitType.Army, TerritoryId = region, IsHostile = false },
                new() { Id = Guid.NewGuid(), Nation = Nation.Russia, UnitType = UnitType.Army, TerritoryId = region, IsHostile = false }
            },
            TerritoryStates = TerritoryData.AllTerritories.Select(t => new TerritoryState { TerritoryId = t.Id }).ToList(),
            Bonds = new List<Bond>(),
            Actions = new List<GameAction>()
        };

        await BuildBotService().BotManeuver(null, game, russia, bot);

        var entries = game.Actions.Where(a => a.ActionType is "ToggleHostility" or "MoveArmy").Select(a => a.ActionType).ToList();
        Assert.Equal(new[] { "ToggleHostility", "MoveArmy", "ToggleHostility", "MoveArmy" }, entries);

        foreach (var stay in game.Actions.Where(a => a.ActionType == "MoveArmy"))
        {
            Assert.Contains("\"SourceIsHostile\":true", stay.Metadata);
        }
        Assert.All(game.Units, u => Assert.True(u.IsHostile && u.HasMoved));
    }
}
