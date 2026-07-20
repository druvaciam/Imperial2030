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

namespace Imperial2030.Tests
{
    public class BotGameTests
    {
        private readonly ITestOutputHelper _output;

        public BotGameTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private ApplicationDbContext GetDbContext(string dbName)
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(databaseName: dbName)
                .Options;
            return new ApplicationDbContext(options);
        }

        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        public async Task TestFullBotGameFinishes(int playerCount)
        {
            string dbName = Guid.NewGuid().ToString();
            var context = GetDbContext(dbName);
            
            var mockHub = new Mock<IHubContext<Imperial2030.Server.Hubs.GameHub>>();
            var mockClients = new Mock<IHubClients>();
            var mockClientProxy = new Mock<IClientProxy>();

            mockHub.Setup(h => h.Clients).Returns(mockClients.Object);
            mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(mockClientProxy.Object);
            mockClients.Setup(c => c.All).Returns(mockClientProxy.Object);

            var mockScopeFactory = new Mock<IServiceScopeFactory>();
            mockScopeFactory.Setup(s => s.CreateScope()).Returns(() => {
                var scope = new Mock<IServiceScope>();
                var mockServiceProvider = new Mock<IServiceProvider>();
                
                // Return a new context instance with the same dbName
                var scopeContext = GetDbContext(dbName);
                mockServiceProvider.Setup(sp => sp.GetService(typeof(ApplicationDbContext))).Returns(scopeContext);
                
                scope.Setup(s => s.ServiceProvider).Returns(mockServiceProvider.Object);
                return scope.Object;
            });

            var botService = new BotService(mockScopeFactory.Object, mockHub.Object);
            botService.SkipDelays = true;

            // Mock UserManager
            var store = new Mock<IUserStore<ApplicationUser>>();
            var mockUserManager = new Mock<UserManager<ApplicationUser>>(store.Object, null, null, null, null, null, null, null, null);

            var mockPresenceTracker = new Mock<PresenceTracker>();

            var gamesController = new GamesController(context, mockUserManager.Object, mockHub.Object, mockPresenceTracker.Object, botService);
            
            var userId = "host-user-id";
            var httpContext = new DefaultHttpContext();
            var claims = new List<Claim> { new Claim(ClaimTypes.NameIdentifier, userId) };
            var identity = new ClaimsIdentity(claims, "TestAuthType");
            var user = new ClaimsPrincipal(identity);
            httpContext.User = user;

            var routeData = new Microsoft.AspNetCore.Routing.RouteData();
            var actionDescriptor = new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor();
            var actionContext = new ActionContext(httpContext, routeData, actionDescriptor);
            gamesController.ControllerContext = new ControllerContext(actionContext);

            // 1. Create Game
            var createReq = new CreateGameRequest { Name = "BotGame", MaxPlayers = playerCount, IsPrivate = false };
            var createRes = await gamesController.CreateGame(createReq);
            var createdAtActionRes = Assert.IsType<CreatedAtActionResult>(createRes.Result);
            var gameDto = Assert.IsType<GameDto>(createdAtActionRes.Value);
            var gameId = gameDto.Id;

            // 2. Add (playerCount - 1) bots (host is the first player)
            for (int i = 0; i < playerCount - 1; i++)
            {
                await gamesController.AddBot(gameId);
            }

            // 2.5 Make the host a bot too so the test runs fully automated
            var host = context.Players.First(p => p.GameId == gameId && p.UserId == userId);
            host.IsBot = true;
            host.BotName = "HostBot";
            await context.SaveChangesAsync();

            // 3. Start Game
            await gamesController.StartGame(gameId);
            
            // Make everyone a bot so the game can play itself
            var allPlayers = context.Players.Where(p => p.GameId == gameId).ToList();
            foreach(var p in allPlayers) p.IsBot = true;
            await context.SaveChangesAsync();

            // 4. Play game
            int maxTurns = 5000;
            int turns = 0;
            
            while (turns < maxTurns)
            {
                var game = context.Games.AsNoTracking().Include(g => g.NationStates).FirstOrDefault(g => g.Id == gameId);
                Assert.NotNull(game);

                if (game.Status == GameStatus.Finished)
                {
                    _output.WriteLine($"Game finished successfully after {turns} actions.");
                    break;
                }

                if (turns % 100 == 0) 
                {
                    var maxPower = game.NationStates.Max(ns => ns.Power);
                    var currentNation = game.CurrentTurnNation;
                    var currentNs = game.NationStates.FirstOrDefault(n => n.Nation == currentNation);
                    var controllerId = currentNs?.ControllerId;
                    var ctrlPlayer = context.Players.AsNoTracking().FirstOrDefault(p => p.Id == controllerId);
                    
                    _output.WriteLine($"Turn {turns}, Max Power: {maxPower}, Current Nation: {currentNation}, Controller: {controllerId}, IsBot: {ctrlPlayer?.IsBot}, IsInvestor: {game.IsInvestorTurn}");
                }

                // Call bot service
                await botService.TryPlayBotTurnAsync(gameId);
                
                turns++;
            }

            // Assert that the game is indeed finished
            var finalGame = context.Games.AsNoTracking().FirstOrDefault(g => g.Id == gameId);
            Assert.NotNull(finalGame);
            Assert.Equal(GameStatus.Finished, finalGame.Status);
        }
    }
}
