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
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Imperial2030.Tests
{
    public class PauseGameTests
    {
        private ApplicationDbContext GetDbContext(string dbName)
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(databaseName: dbName)
                .Options;
            return new ApplicationDbContext(options);
        }

        private (GamesController controller, ApplicationDbContext context, Mock<IHubContext<Imperial2030.Server.Hubs.GameHub>> mockHub, BotService botService) CreateController(string dbName, string userId)
        {
            var context = GetDbContext(dbName);

            var services = new ServiceCollection();
            services.AddSingleton(context);
            services.AddLogging();
            var serviceProvider = services.BuildServiceProvider();
            var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

            var mockHub = new Mock<IHubContext<Imperial2030.Server.Hubs.GameHub>>();
            var mockClients = new Mock<IHubClients>();
            var mockClientProxy = new Mock<IClientProxy>();
            mockHub.Setup(h => h.Clients).Returns(mockClients.Object);
            mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(mockClientProxy.Object);
            mockClients.Setup(c => c.All).Returns(mockClientProxy.Object);

            var mockUserStore = new Mock<IUserStore<ApplicationUser>>();
            var userManager = new UserManager<ApplicationUser>(mockUserStore.Object, null!, null!, null!, null!, null!, null!, null!, null!);

            var presenceTracker = new PresenceTracker();
            var mockNotificationService = new Mock<INotificationService>();
            var botService = new BotService(scopeFactory, mockHub.Object, null!, new Mock<ILogger<BotService>>().Object);
            botService.SkipDelays = true;

            var controller = new GamesController(context, userManager, mockHub.Object, presenceTracker, botService, mockNotificationService.Object);

            var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(ClaimTypes.Name, "HumanPlayer")
            }, "TestAuth"));

            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = user }
            };

            return (controller, context, mockHub, botService);
        }

        [Fact]
        public async Task TogglePause_TogglesPausedState_ForSinglePlayerGame()
        {
            string dbName = Guid.NewGuid().ToString();
            string userId = Guid.NewGuid().ToString();
            var (controller, context, _, _) = CreateController(dbName, userId);

            var human = new ApplicationUser { Id = userId, UserName = "HumanPlayer" };
            context.Users.Add(human);

            var game = new Game
            {
                Id = Guid.NewGuid(),
                Name = "Single Player Game",
                Status = GameStatus.InProgress,
                IsPaused = false
            };
            context.Games.Add(game);

            var player1 = new Player { Id = Guid.NewGuid(), GameId = game.Id, UserId = userId, User = human, IsHost = true, IsBot = false };
            var bot1 = new Player { Id = Guid.NewGuid(), GameId = game.Id, IsBot = true, BotName = "Bot 1" };
            context.Players.AddRange(player1, bot1);
            await context.SaveChangesAsync();

            // Act 1: Pause the game
            var result1 = await controller.TogglePause(game.Id);
            var okResult1 = Assert.IsType<OkObjectResult>(result1);
            var updatedGame1 = await context.Games.FindAsync(game.Id);
            Assert.NotNull(updatedGame1);
            Assert.True(updatedGame1.IsPaused);

            // Act 2: Resume the game
            var result2 = await controller.TogglePause(game.Id);
            var okResult2 = Assert.IsType<OkObjectResult>(result2);
            var updatedGame2 = await context.Games.FindAsync(game.Id);
            Assert.NotNull(updatedGame2);
            Assert.False(updatedGame2.IsPaused);
        }

        [Fact]
        public async Task TogglePause_Rejects_MultiplayerGame()
        {
            string dbName = Guid.NewGuid().ToString();
            string userId1 = Guid.NewGuid().ToString();
            string userId2 = Guid.NewGuid().ToString();
            var (controller, context, _, _) = CreateController(dbName, userId1);

            var game = new Game
            {
                Id = Guid.NewGuid(),
                Name = "Multiplayer Game",
                Status = GameStatus.InProgress,
                IsPaused = false
            };
            context.Games.Add(game);

            var player1 = new Player { Id = Guid.NewGuid(), GameId = game.Id, UserId = userId1, IsHost = true, IsBot = false };
            var player2 = new Player { Id = Guid.NewGuid(), GameId = game.Id, UserId = userId2, IsHost = false, IsBot = false };
            context.Players.AddRange(player1, player2);
            await context.SaveChangesAsync();

            // Act: Attempt to pause
            var result = await controller.TogglePause(game.Id);
            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal("Pause is only available in single-player games.", badRequest.Value);
        }

        [Fact]
        public async Task BotService_Stops_WhenGameIsPaused()
        {
            string dbName = Guid.NewGuid().ToString();
            string userId = Guid.NewGuid().ToString();
            var (controller, context, _, botService) = CreateController(dbName, userId);

            var game = new Game
            {
                Id = Guid.NewGuid(),
                Name = "Paused Bot Game",
                Status = GameStatus.InProgress,
                CurrentTurnNation = Nation.Russia,
                IsPaused = true,
                TurnCount = 1
            };
            context.Games.Add(game);

            var bot = new Player { Id = Guid.NewGuid(), GameId = game.Id, IsBot = true, BotName = "Bot 1" };
            var ns = new NationState { Id = Guid.NewGuid(), GameId = game.Id, Nation = Nation.Russia, ControllerId = bot.Id, Controller = bot };
            context.Players.Add(bot);
            context.NationStates.Add(ns);
            await context.SaveChangesAsync();

            // Act: Try to play bot turn on a paused game
            await botService.TryPlayBotTurnAsync(game.Id);

            // Assert: Game turn nation and turn count remain untouched
            var reloadedGame = await context.Games.FindAsync(game.Id);
            Assert.NotNull(reloadedGame);
            Assert.Equal(1, reloadedGame.TurnCount);
            Assert.Equal(Nation.Russia, reloadedGame.CurrentTurnNation);
        }
    }
}
