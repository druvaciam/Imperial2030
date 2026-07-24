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
        [InlineData(2, false)]
        [InlineData(2, true)]
        [InlineData(3, false)]
        [InlineData(3, true)]
        [InlineData(4, false)]
        [InlineData(4, true)]
        [InlineData(5, false)]
        [InlineData(5, true)]
        [InlineData(6, false)]
        [InlineData(6, true)]
        public async Task TestFullBotGameFinishes(int playerCount, bool isVariantActive)
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
            mockScopeFactory.Setup(s => s.CreateScope()).Returns(() =>
            {
                var scope = new Mock<IServiceScope>();
                var mockServiceProvider = new Mock<IServiceProvider>();

                // Return a new context instance with the same dbName
                var scopeContext = GetDbContext(dbName);
                mockServiceProvider.Setup(sp => sp.GetService(typeof(ApplicationDbContext))).Returns(scopeContext);

                scope.Setup(s => s.ServiceProvider).Returns(mockServiceProvider.Object);
                return scope.Object;
            });

            var botService = new BotService(mockScopeFactory.Object, mockHub.Object, new System.Collections.Generic.List<Imperial2030.Server.Services.Bots.IBotStrategy> { new Imperial2030.Server.Services.Bots.Strategies.DefaultBotStrategy() });
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
            var createReq = new CreateGameRequest { Name = "BotGame", MaxPlayers = playerCount, IsPrivate = false, VariantBonusOnlyForTaxIncreases = isVariantActive };
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
            foreach (var p in allPlayers) p.IsBot = true;
            await context.SaveChangesAsync();

            // 4. Play game
            int maxTurns = 30000;
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

        [Fact(Skip = "Takes a long time, run manually when checking organic Swiss bank scenario")]
        public async Task TestBotPlaysUntilSwissBankWins()
        {
            var stopWatch = System.Diagnostics.Stopwatch.StartNew();
            bool foundScenario = false;
            int gameCount = 0;

            while (stopWatch.Elapsed.TotalSeconds < 120)
            {
                gameCount++;
                var dbName = Guid.NewGuid().ToString();
                using var context = GetDbContext(dbName);

                var mockHub = new Mock<IHubContext<Imperial2030.Server.Hubs.GameHub>>();
                var mockClients = new Mock<IHubClients>();
                var mockClientProxy = new Mock<IClientProxy>();
                mockHub.Setup(h => h.Clients).Returns(mockClients.Object);
                mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(mockClientProxy.Object);
                mockClients.Setup(c => c.All).Returns(mockClientProxy.Object);

                var mockScopeFactory = new Mock<IServiceScopeFactory>();
                mockScopeFactory.Setup(s => s.CreateScope()).Returns(() =>
                {
                    var scope = new Mock<IServiceScope>();
                    var mockServiceProvider = new Mock<IServiceProvider>();
                    var scopeContext = GetDbContext(dbName);
                    mockServiceProvider.Setup(sp => sp.GetService(typeof(ApplicationDbContext))).Returns(scopeContext);
                    scope.Setup(s => s.ServiceProvider).Returns(mockServiceProvider.Object);
                    return scope.Object;
                });

                var botService = new BotService(mockScopeFactory.Object, mockHub.Object, new System.Collections.Generic.List<Imperial2030.Server.Services.Bots.IBotStrategy> { new Imperial2030.Server.Services.Bots.Strategies.DefaultBotStrategy() });
                botService.SkipDelays = true;

                var store = new Mock<IUserStore<ApplicationUser>>();
                var mockUserManager = new Mock<UserManager<ApplicationUser>>(store.Object, null, null, null, null, null, null, null, null);
                var mockPresenceTracker = new Mock<PresenceTracker>();

                var gamesController = new GamesController(context, mockUserManager.Object, mockHub.Object, mockPresenceTracker.Object, botService);

                var userId = "host-user-id";
                var httpContext = new DefaultHttpContext();
                var claims = new List<Claim> { new Claim(ClaimTypes.NameIdentifier, userId) };
                var identity = new ClaimsIdentity(claims, "TestAuthType");
                httpContext.User = new ClaimsPrincipal(identity);

                gamesController.ControllerContext = new ControllerContext(new ActionContext(httpContext, new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor()));

                // 1. Create 6-player game
                var createReq = new CreateGameRequest { Name = "BotGameSwiss", MaxPlayers = 6, IsPrivate = false };
                var createRes = await gamesController.CreateGame(createReq);
                var gameId = Assert.IsType<GameDto>(Assert.IsType<CreatedAtActionResult>(createRes.Result).Value).Id;

                // 2. Add 5 bots
                for (int i = 0; i < 5; i++)
                {
                    await gamesController.AddBot(gameId);
                }

                // 3. Make host a bot too
                var host = context.Players.First(p => p.GameId == gameId && p.UserId == userId);
                host.IsBot = true;
                host.BotName = "HostBot";
                await context.SaveChangesAsync();

                // 4. Start Game
                await gamesController.StartGame(gameId);

                var players = context.Players.Where(p => p.GameId == gameId).ToList();
                var observedSwissBanks = new HashSet<Guid>();

                // 5. Play game
                int maxTurns = 5000;
                int turns = 0;

                while (turns < maxTurns)
                {
                    var game = context.Games.AsNoTracking().Include(g => g.NationStates).FirstOrDefault(g => g.Id == gameId);
                    if (game == null || game.Status == GameStatus.Finished) break;

                    // Check for Swiss Banks
                    var controlledNationsByPlayer = game.NationStates.Select(ns => ns.ControllerId).Where(id => id.HasValue).Select(id => id.Value).Distinct().ToList();
                    foreach (var p in players)
                    {
                        if (!controlledNationsByPlayer.Contains(p.Id))
                        {
                            observedSwissBanks.Add(p.Id);
                        }
                    }

                    await botService.TryPlayBotTurnAsync(gameId);
                    turns++;
                }

                var finalGame = context.Games.AsNoTracking().FirstOrDefault(g => g.Id == gameId);
                if (finalGame?.Status == GameStatus.Finished)
                {
                    // Calculate scores
                    var bonds = context.Bonds.Where(b => b.GameId == gameId && b.HolderId != null).ToList();
                    var nations = context.NationStates.Where(n => n.GameId == gameId).ToList();

                    var scores = new Dictionary<Guid, int>();
                    foreach (var p in players)
                    {
                        // Refresh player cash
                        var finalPlayer = context.Players.AsNoTracking().First(x => x.Id == p.Id);
                        int score = finalPlayer.Cash;
                        var playerBonds = bonds.Where(b => b.HolderId == p.Id).ToList();
                        foreach (var b in playerBonds)
                        {
                            var nation = nations.First(n => n.Nation == b.Nation);
                            int multiplier = nation.Power / 5;
                            score += b.Cost * multiplier;
                        }
                        scores[p.Id] = score;
                    }

                    var winnerId = scores.OrderByDescending(kvp => kvp.Value).First().Key;
                    if (observedSwissBanks.Contains(winnerId))
                    {
                        foundScenario = true;
                        _output.WriteLine($"Found a Swiss Bank winner in game {gameCount} after {turns} turns!");
                        break;
                    }
                }
            }

            Assert.True(foundScenario, $"Ran {gameCount} games in 30 seconds but did not observe a Swiss Bank player winning.");
        }

        [Fact]
        public async Task TestBotPassesInvestmentToPendingInvestor()
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
                var scopeContext = GetDbContext(dbName);
                mockServiceProvider.Setup(sp => sp.GetService(typeof(ApplicationDbContext))).Returns(scopeContext);
                scope.Setup(s => s.ServiceProvider).Returns(mockServiceProvider.Object);
                return scope.Object;
            });

            var botService = new BotService(mockScopeFactory.Object, mockHub.Object, new System.Collections.Generic.List<Imperial2030.Server.Services.Bots.IBotStrategy> { new Imperial2030.Server.Services.Bots.Strategies.DefaultBotStrategy() }) { SkipDelays = true };

            var gameId = Guid.NewGuid();
            var humanId = Guid.NewGuid();
            var botId = Guid.NewGuid();

            var game = new Game
            {
                Id = gameId,
                Status = GameStatus.InProgress,
                IsInvestorTurn = true,
                ActingPlayerId = botId,
                PendingInvestorIds = new List<Guid> { humanId },
                InvestorCardHolderId = humanId
            };
            context.Games.Add(game);

            context.Players.Add(new Player { Id = humanId, GameId = gameId, IsBot = false });
            context.Players.Add(new Player { Id = botId, GameId = gameId, IsBot = true, BotName = "Bot Swiss" });

            await context.SaveChangesAsync();

            await botService.TryPlayBotTurnAsync(gameId);

            var updatedGame = await GetDbContext(dbName).Games.Include(g => g.Players).FirstAsync(g => g.Id == gameId);
            
            Assert.True(updatedGame.IsInvestorTurn);
            Assert.Equal(humanId, updatedGame.ActingPlayerId);
            Assert.Empty(updatedGame.PendingInvestorIds);
            Assert.Equal(humanId, updatedGame.InvestorCardHolderId);
        }

        [Fact]
        public async Task TestBotPassesInvestmentToPendingSwissBank()
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
                var scopeContext = GetDbContext(dbName);
                mockServiceProvider.Setup(sp => sp.GetService(typeof(ApplicationDbContext))).Returns(scopeContext);
                scope.Setup(s => s.ServiceProvider).Returns(mockServiceProvider.Object);
                return scope.Object;
            });

            var botService = new BotService(mockScopeFactory.Object, mockHub.Object, new System.Collections.Generic.List<Imperial2030.Server.Services.Bots.IBotStrategy> { new Imperial2030.Server.Services.Bots.Strategies.DefaultBotStrategy() }) { SkipDelays = true };

            var gameId = Guid.NewGuid();
            var humanId = Guid.NewGuid();
            var bot1Id = Guid.NewGuid();
            var bot2Id = Guid.NewGuid();

            var game = new Game
            {
                Id = gameId,
                Status = GameStatus.InProgress,
                IsInvestorTurn = true,
                ActingPlayerId = bot1Id,
                PendingInvestorIds = new List<Guid> { bot2Id, humanId },
                InvestorCardHolderId = humanId
            };
            context.Games.Add(game);

            context.Players.Add(new Player { Id = humanId, GameId = gameId, IsBot = false });
            context.Players.Add(new Player { Id = bot1Id, GameId = gameId, IsBot = true, BotName = "Bot Swiss 1" });
            context.Players.Add(new Player { Id = bot2Id, GameId = gameId, IsBot = true, BotName = "Bot Swiss 2" });

            await context.SaveChangesAsync();

            await botService.TryPlayBotTurnAsync(gameId);

            var updatedGame = await GetDbContext(dbName).Games.Include(g => g.Players).FirstAsync(g => g.Id == gameId);
            
            Assert.True(updatedGame.IsInvestorTurn);
            Assert.Equal(humanId, updatedGame.ActingPlayerId);
            Assert.Empty(updatedGame.PendingInvestorIds);
            Assert.Equal(humanId, updatedGame.InvestorCardHolderId);
        }
        [Fact]
        public async Task TestConcurrentBotTurnsArePreventedByLock()
        {
            string dbName = Guid.NewGuid().ToString();
            var context = GetDbContext(dbName);
            
            var mockHub = new Mock<IHubContext<Imperial2030.Server.Hubs.GameHub>>();
            var mockClients = new Mock<IHubClients>();
            var mockClientProxy = new Mock<IClientProxy>();
            mockHub.Setup(h => h.Clients).Returns(mockClients.Object);
            mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(mockClientProxy.Object);

            var scopeFactory = new Mock<IServiceScopeFactory>();
            var scope = new Mock<IServiceScope>();
            var sp = new Mock<IServiceProvider>();
            
            scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);
            scope.Setup(s => s.ServiceProvider).Returns(sp.Object);
            sp.Setup(s => s.GetService(typeof(ApplicationDbContext))).Returns(() => GetDbContext(dbName));

            var botService = new BotService(scopeFactory.Object, mockHub.Object, new System.Collections.Generic.List<Imperial2030.Server.Services.Bots.IBotStrategy> { new Imperial2030.Server.Services.Bots.Strategies.DefaultBotStrategy() })
            {
                SkipDelays = true // Important: no delays, they execute instantly
            };

            var gameId = Guid.NewGuid();
            var botId = Guid.NewGuid();
            var humanId = Guid.NewGuid();

            var game = new Game
            {
                Id = gameId,
                Name = "Test Game",
                Status = GameStatus.InProgress,
                CurrentTurnNation = Nation.Russia
            };

            // Add Bot controlling Russia
            game.Players.Add(new Player { Id = botId, GameId = gameId, IsBot = true, BotName = "Bot Alpha", Cash = 10 });
            game.NationStates.Add(new NationState { Nation = Nation.Russia, ControllerId = botId, Treasury = 0 });

            // Add Human controlling China
            game.Players.Add(new Player { Id = humanId, GameId = gameId, IsBot = false, Cash = 10 });
            game.NationStates.Add(new NationState { Nation = Nation.China, ControllerId = humanId, Treasury = 0 });

            context.Games.Add(game);
            await context.SaveChangesAsync();

            // Act: Fire 20 concurrent tasks!
            var tasks = new List<Task>();
            for (int i = 0; i < 20; i++)
            {
                tasks.Add(Task.Run(() => botService.TryPlayBotTurnAsync(gameId)));
            }

            await Task.WhenAll(tasks);

            // Assert
            var updatedGame = await GetDbContext(dbName).Games.Include(g => g.Actions).FirstAsync(g => g.Id == gameId);

            // Turn should have advanced to China (the human)
            Assert.Equal(Nation.China, updatedGame.CurrentTurnNation);

            // Only ONE action (Move/EndTurn) should have been logged for Russia.
            // A typical turn produces exactly 2 actions: Move and EndTurn.
            // If the lock failed, there would be many more actions or a DbUpdateConcurrencyException.
            Assert.Equal(2, updatedGame.Actions.Count);
        }
    }
}
