using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Imperial2030.Server.Controllers;
using Imperial2030.Server.Data;
using Imperial2030.Server.Models;
using Imperial2030.Server.Services;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Imperial2030.Tests;

/// <summary>
/// A rondel move that lands on Maneuver with nothing of a kind to move ends that phase at once, and a
/// phase end places flags. That flag pass reads and writes <c>Game.TerritoryStates</c>, so the endpoint
/// must load them: with the collection left unloaded it looks empty, every occupied region gets a SECOND
/// state row, and each row with a controller counts as a flag at the next taxation.
///
/// Seen as `TestImportFromExportedJson` replaying a game with higher tax revenue than the original -
/// Ukraine and Turkey each held two rows for Europe.
/// </summary>
public class MoveNationFlagRowsTests
{
    [Fact]
    public async Task LandingOnManeuverWithNoFleetsDoesNotDuplicateTerritoryStateRows()
    {
        var db = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var gameId = Guid.NewGuid();
        var userId = "human-1";
        var neutral = TerritoryData.AllTerritories.First(t => t.Type == TerritoryType.Land && t.Nation == null).Id;

        await using (var seed = new ApplicationDbContext(db))
        {
            var player = new Player { Id = Guid.NewGuid(), GameId = gameId, UserId = userId, IsBot = false, Cash = 10 };
            seed.Games.Add(new Game
            {
                Id = gameId, Name = "Flag rows", Status = GameStatus.InProgress, CurrentTurnNation = Nation.Russia,
                Players = new List<Player> { player },
                NationStates = Enum.GetValues<Nation>().Select(n => new NationState { GameId = gameId, Nation = n, ControllerId = player.Id, RondelPosition = RondelData.TaxationSlot, Treasury = 5 }).ToList(),
                // One army already holding a neutral region, whose flag is already placed.
                Units = new List<Unit> { new() { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.Russia, UnitType = UnitType.Army, TerritoryId = neutral } },
                TerritoryStates = TerritoryData.AllTerritories.Select(t => new TerritoryState { GameId = gameId, TerritoryId = t.Id, Controller = t.Id == neutral ? Nation.Russia : null }).ToList(),
                Bonds = new List<Bond>(), Actions = new List<GameAction>()
            });
            await seed.SaveChangesAsync();
        }

        await using var context = new ApplicationDbContext(db);
        var hub = new Mock<IHubContext<Imperial2030.Server.Hubs.GameHub>>();
        var clients = new Mock<IHubClients>();
        hub.Setup(h => h.Clients).Returns(clients.Object);
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(new Mock<IClientProxy>().Object);
        clients.Setup(c => c.All).Returns(new Mock<IClientProxy>().Object);
        var botService = new BotService(new Mock<IServiceScopeFactory>().Object, hub.Object,
            new List<Imperial2030.Server.Services.Bots.IBotStrategy> { new Imperial2030.Server.Services.Bots.Strategies.DefaultBotStrategy() },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BotService>.Instance) { SkipDelays = true };
        var store = new Mock<IUserStore<ApplicationUser>>();
        var userManager = new Mock<UserManager<ApplicationUser>>(store.Object, null, null, null, null, null, null, null, null);
        var controller = new GamesController(context, userManager.Object, hub.Object, new Mock<PresenceTracker>().Object, botService, new Mock<INotificationService>().Object);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "test")) }
        };

        // Taxation -> Maneuver 1 is three spaces, free. Russia has no fleet, so the Fleets phase ends at once.
        var result = await controller.MoveNation(gameId, Nation.Russia, RondelData.ManeuverSlot1);
        Assert.IsType<OkResult>(result);

        await using var check = new ApplicationDbContext(db);
        var rows = await check.TerritoryStates.Where(t => t.GameId == gameId && t.TerritoryId == neutral).ToListAsync();
        Assert.Single(rows);
        Assert.Equal(Nation.Russia, rows[0].Controller);
        Assert.Equal(ManeuverPhase.Armies, (await check.Games.FirstAsync(g => g.Id == gameId)).CurrentManeuverPhase);
        Assert.DoesNotContain(await check.GameActions.Where(a => a.GameId == gameId).ToListAsync(), a => a.ActionType == "FlagPlacement");
    }
}
