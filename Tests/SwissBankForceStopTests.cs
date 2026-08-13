using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Imperial2030.Server.Controllers;
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
using Moq;
using Xunit;
using Imperial2030.Server.Services;
using Xunit.Abstractions;
using Microsoft.Extensions.Logging;

namespace Imperial2030.Tests
{
    public class SwissBankForceStopTests
    {
        private ApplicationDbContext GetDbContext(string dbName)
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(databaseName: dbName)
                .Options;
            return new ApplicationDbContext(options);
        }

        [Fact]
        public async Task TestBotSwissBankForcesStop()
        {
            string dbName = Guid.NewGuid().ToString();
            var context = GetDbContext(dbName);

            var mockHub = new Mock<IHubContext<Imperial2030.Server.Hubs.GameHub>>();
            var mockClients = new Mock<IHubClients>();
            var mockClientProxy = new Mock<IClientProxy>();
            mockHub.Setup(h => h.Clients).Returns(mockClients.Object);
            mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(mockClientProxy.Object);

            var mockScopeFactory = new Mock<IServiceScopeFactory>();
            mockScopeFactory.Setup(s => s.CreateScope()).Returns(() =>
            {
                var scope = new Mock<IServiceScope>();
                var mockServiceProvider = new Mock<IServiceProvider>();
                mockServiceProvider.Setup(sp => sp.GetService(typeof(ApplicationDbContext))).Returns(GetDbContext(dbName));
                mockServiceProvider.Setup(sp => sp.GetService(typeof(Imperial2030.Server.Services.INotificationService))).Returns(new Moq.Mock<Imperial2030.Server.Services.INotificationService>().Object);
                scope.Setup(s => s.ServiceProvider).Returns(mockServiceProvider.Object);
                return scope.Object;
            });

            var mockLogger = new Mock<ILogger<BotService>>();
            var botService = new BotService(mockScopeFactory.Object, mockHub.Object, [new Imperial2030.Server.Services.Bots.Strategies.DefaultBotStrategy()], mockLogger.Object) { SkipDelays = true };

            // Mock UserManager
            var store = new Mock<IUserStore<ApplicationUser>>();
            var mockUserManager = new Mock<UserManager<ApplicationUser>>(store.Object, null, null, null, null, null, null, null, null);
            var mockPresenceTracker = new Mock<PresenceTracker>();

            var gamesController = new GamesController(context, mockUserManager.Object, mockHub.Object, mockPresenceTracker.Object, botService, new Moq.Mock<Imperial2030.Server.Services.INotificationService>().Object);

            var botId = Guid.NewGuid();
            var humanId = Guid.NewGuid();

            var botPlayer = new Player { Id = botId, UserId = "bot-user", BotName = "Bot Alpha", IsBot = true, Cash = 0 };
            var humanPlayer = new Player { Id = humanId, UserId = "human-user", IsBot = false, Cash = 20 };

            var gameId = Guid.NewGuid();
            var game = new Game
            {
                Id = gameId,
                Name = "Test Game",
                Status = GameStatus.InProgress,
                CurrentTurnNation = Nation.Europe,
                Players = new List<Player> { botPlayer, humanPlayer },
                NationStates = new List<NationState>
                {
                    new NationState { Nation = Nation.Europe, ControllerId = humanId, Treasury = 10, RondelPosition = 3 /* Maneuver */, Power = 0 },
                    new NationState { Nation = Nation.Russia, ControllerId = botId, Treasury = 0, RondelPosition = 0, Power = 0 }
                },
                Bonds = new List<Bond>
                {
                    new Bond { Id = Guid.NewGuid(), Nation = Nation.Europe, Cost = 9, Interest = 4, HolderId = botId } // Bot holds Europe bond
                }
            };
            context.Games.Add(game);
            await context.SaveChangesAsync();

            // Set context for human player
            var httpContext = new DefaultHttpContext();
            var claims = new List<Claim> { new Claim(ClaimTypes.NameIdentifier, "human-user") };
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuthType"));
            gamesController.ControllerContext = new ControllerContext(new ActionContext(httpContext, new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor()));

            // Act: Human moves Europe past Investor (Slot 4) to Import (Slot 5)
            // Current is 3. Target is 5. Crosses 4.
            // Europe has 10 Treasury, which is >= 4 (total interest).
            // Bot holds bond, Bot controls Russia. Wait, Bot controls Russia, so Bot is NOT a Swiss Bank!
            // Swiss Bank player = NO controlled governments.

            // Let's modify: Bot should NOT control Russia to be Swiss Bank.
            var russiaNs = game.NationStates.First(n => n.Nation == Nation.Russia);
            russiaNs.ControllerId = humanId; // Now human controls both Europe and Russia
            await context.SaveChangesAsync();

            // Bot is now a Swiss Bank and holds Europe bond!
            var result = await gamesController.MoveNation(gameId, Nation.Europe, 5); // Try to move to Import

            Assert.IsType<OkResult>(result);

            // Reload game to check state
            var updatedGame = await context.Games.Include(g => g.NationStates).FirstAsync(g => g.Id == gameId);

            // The move should have been intercepted
            var debugInfo = $"crossingInvestor={result is OkResult}, PendingNation={updatedGame.PendingSwissBankForceNation}, Players={game.Players.Count}, Nations={game.NationStates.Count}, BotController={game.NationStates.Any(ns => ns.ControllerId == botId)}, HumanController={game.NationStates.Any(ns => ns.ControllerId == humanId)}";
            Assert.Equal(Nation.Europe.ToString(), updatedGame.PendingSwissBankForceNation?.ToString() ?? debugInfo);
            Assert.Contains(botId, updatedGame.PendingSwissBankResponders);

            // Act 2: Wait for the background task triggered by MoveNation to complete
            // (MoveNation calls botService.TriggerBotTurn which runs in Task.Run)
            await Task.Delay(1500); 

            // Reload game again
            context.ChangeTracker.Clear();
            updatedGame = await context.Games.Include(g => g.NationStates).FirstAsync(g => g.Id == gameId);

            // Bot should have forced stop AND played its investor turn
            var euNs = updatedGame.NationStates.First(n => n.Nation == Nation.Europe);
            var actionsText = string.Join("\n", updatedGame.Actions.Select(a => $"{a.ActionType}: {a.Message}"));
            Assert.True(updatedGame.PendingSwissBankForceNation == null, $"Failed! PendingNation is {updatedGame.PendingSwissBankForceNation}. Actions: {actionsText}");
            Assert.Empty(updatedGame.PendingSwissBankResponders);
            Assert.Equal(4, euNs.RondelPosition); // Forced to stop on Investor (Slot 4)
            
            // Because the bot forced the stop, it is the Swiss Bank player and therefore eligible to invest.
            // The background BotService loop will automatically detect game.IsInvestorTurn and play the bot's turn!
            // So by the time it finishes, IsInvestorTurn should be false (phase complete).
            Assert.False(updatedGame.IsInvestorTurn, "Investor turn should have been auto-played by the bot.");
        }
    }
}
