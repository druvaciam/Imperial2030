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
                mockServiceProvider.Setup(sp => sp.GetService(typeof(Imperial2030.Server.Services.INotificationService))).Returns(new Moq.Mock<Imperial2030.Server.Services.INotificationService>().Object);

                scope.Setup(s => s.ServiceProvider).Returns(mockServiceProvider.Object);
                return scope.Object;
            });

            var mockLogger = new Mock<ILogger<BotService>>();
            var botService = new BotService(mockScopeFactory.Object, mockHub.Object, [new Imperial2030.Server.Services.Bots.Strategies.DefaultBotStrategy()], mockLogger.Object);
            botService.SkipDelays = true;

            // Mock UserManager
            var store = new Mock<IUserStore<ApplicationUser>>();
            var mockUserManager = new Mock<UserManager<ApplicationUser>>(store.Object, null, null, null, null, null, null, null, null);

            var mockPresenceTracker = new Mock<PresenceTracker>();

            var gamesController = new GamesController(context, mockUserManager.Object, mockHub.Object, mockPresenceTracker.Object, botService, new Moq.Mock<Imperial2030.Server.Services.INotificationService>().Object);

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

            int timeoutTicks = 0;
            while (timeoutTicks < 2000)
            {
                using var scope = mockScopeFactory.Object.CreateScope();
                var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var game = ctx.Games.AsNoTracking().FirstOrDefault(g => g.Id == gameId);

                if (game == null || game.Status == GameStatus.Finished) break;

                if (timeoutTicks % 10 == 0)
                {
                    var fullGame = ctx.Games.AsNoTracking().Include(g => g.NationStates).FirstOrDefault(g => g.Id == gameId);
                    if (fullGame != null)
                    {
                        var maxPower = fullGame.NationStates.Any() ? fullGame.NationStates.Max(ns => ns.Power) : 0;
                        var currentNation = fullGame.CurrentTurnNation;
                        var currentNs = fullGame.NationStates.FirstOrDefault(n => n.Nation == currentNation);
                        var controllerId = currentNs?.ControllerId;
                        var ctrlPlayer = ctx.Players.AsNoTracking().FirstOrDefault(p => p.Id == controllerId);

                        _output.WriteLine($"Tick {timeoutTicks}, Max Power: {maxPower}, Current Nation: {currentNation}, Controller: {controllerId}, IsBot: {ctrlPlayer?.IsBot}, IsInvestor: {fullGame.IsInvestorTurn}");
                    }
                }

                await Task.Delay(10);
                timeoutTicks++;
            }

            // Assert that the game is indeed finished
            var finalGame = context.Games.AsNoTracking().FirstOrDefault(g => g.Id == gameId);
            Assert.NotNull(finalGame);
            Assert.Equal(GameStatus.Finished, finalGame.Status);

            using var metricScope = mockScopeFactory.Object.CreateScope();
            var metricCtx = metricScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            int turns = metricCtx.GameActions.Count(a => a.GameId == gameId);
            _output.WriteLine($"Game finished successfully after {turns} actions.");
        }

        [Fact]
        public async Task TestBotPlaysUntilSwissBankWins()
        {
            var stopWatch = System.Diagnostics.Stopwatch.StartNew();
            bool foundScenario = false;
            int gameCount = 0;

            while (stopWatch.Elapsed.TotalSeconds < 150)
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
                    mockServiceProvider.Setup(sp => sp.GetService(typeof(Imperial2030.Server.Services.INotificationService))).Returns(new Moq.Mock<Imperial2030.Server.Services.INotificationService>().Object);
                    scope.Setup(s => s.ServiceProvider).Returns(mockServiceProvider.Object);
                    return scope.Object;
                });

                var mockLogger = new Mock<ILogger<BotService>>();
                var botService = new BotService(mockScopeFactory.Object, mockHub.Object, [new Imperial2030.Server.Services.Bots.Strategies.DefaultBotStrategy()], mockLogger.Object);
                botService.SkipDelays = true;

                var store = new Mock<IUserStore<ApplicationUser>>();
                var mockUserManager = new Mock<UserManager<ApplicationUser>>(store.Object, null, null, null, null, null, null, null, null);
                var mockPresenceTracker = new Mock<PresenceTracker>();

                var gamesController = new GamesController(context, mockUserManager.Object, mockHub.Object, mockPresenceTracker.Object, botService, new Moq.Mock<Imperial2030.Server.Services.INotificationService>().Object);

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
                int timeoutTicks = 0;
                while (timeoutTicks < 2000)
                {
                    using var scope = mockScopeFactory.Object.CreateScope();
                    var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var game = ctx.Games.AsNoTracking().Include(g => g.NationStates).FirstOrDefault(g => g.Id == gameId);
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

                    await Task.Delay(50);
                    timeoutTicks++;
                }

                if (observedSwissBanks.Any())
                    _output.WriteLine($"Swiss Bank player emerged in game {gameCount}!");

                var finalGame = context.Games.AsNoTracking()
                    .Include(g => g.Players)
                    .Include(g => g.NationStates)
                    .Include(g => g.Bonds)
                    .FirstOrDefault(g => g.Id == gameId);
                if (finalGame?.Status == GameStatus.Finished)
                {
                    var rankedPlayers = finalGame.GetRankedPlayers();
                    var winnerId = rankedPlayers.First().Id;
                    if (observedSwissBanks.Contains(winnerId))
                    {
                        using var metricScope = mockScopeFactory.Object.CreateScope();
                        var metricCtx = metricScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                        int actualTurns = metricCtx.GameActions.Count(a => a.GameId == gameId);

                        foundScenario = true;
                        _output.WriteLine($"Found a Swiss Bank winner in game {gameCount} after {actualTurns} turns!");
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
                mockServiceProvider.Setup(sp => sp.GetService(typeof(Imperial2030.Server.Services.INotificationService))).Returns(new Moq.Mock<Imperial2030.Server.Services.INotificationService>().Object);
                scope.Setup(s => s.ServiceProvider).Returns(mockServiceProvider.Object);
                return scope.Object;
            });

            var mockLogger = new Mock<ILogger<BotService>>();

            var botService = new BotService(mockScopeFactory.Object, mockHub.Object, [new Imperial2030.Server.Services.Bots.Strategies.DefaultBotStrategy()], mockLogger.Object) { SkipDelays = true };

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
        public async Task TestBotTurnEndsWhenArmyStuck()
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
                mockServiceProvider.Setup(sp => sp.GetService(typeof(Imperial2030.Server.Services.INotificationService))).Returns(new Moq.Mock<Imperial2030.Server.Services.INotificationService>().Object);
                scope.Setup(s => s.ServiceProvider).Returns(mockServiceProvider.Object);
                return scope.Object;
            });

            var mockLogger = new Mock<ILogger<BotService>>();

            var botService = new BotService(mockScopeFactory.Object, mockHub.Object, [new Imperial2030.Server.Services.Bots.Strategies.DefaultBotStrategy()], mockLogger.Object) { SkipDelays = true };

            var gameId = Guid.NewGuid();
            var botId = Guid.NewGuid();
            var nextPlayerId = Guid.NewGuid();

            var game = new Game
            {
                Id = gameId,
                Status = GameStatus.InProgress,
                CurrentTurnNation = Nation.Russia,
                IsInvestorTurn = false
            };
            context.Games.Add(game);

            context.Players.Add(new Player { Id = botId, GameId = gameId, IsBot = true, BotName = "Bot Russia" });
            context.Players.Add(new Player { Id = nextPlayerId, GameId = gameId, IsBot = true, BotName = "Bot China" });

            context.NationStates.Add(new NationState { Nation = Nation.Russia, GameId = gameId, ControllerId = botId, RondelPosition = 3, HasMovedThisTurn = true }); // Slot 3 is Maneuver
            context.NationStates.Add(new NationState { Nation = Nation.China, GameId = gameId, ControllerId = nextPlayerId, RondelPosition = 2 });

            // Add an army on New Zealand (which is disconnected from everything else if there are no fleets)
            context.Units.Add(new Unit { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.Russia, TerritoryId = "NewZealand", UnitType = UnitType.Army, HasMoved = false });

            await context.SaveChangesAsync();

            // Act
            await botService.TryPlayBotTurnAsync(gameId, singleTurnOnly: true);

            var db = GetDbContext(dbName);
            var updatedGame = await db.Games.FirstAsync(g => g.Id == gameId);
            var updatedArmy = await db.Units.FirstAsync(u => u.Nation == Nation.Russia);

            var actions = await db.GameActions.Where(a => a.GameId == gameId).ToListAsync();
            foreach (var action in actions)
            {
                _output.WriteLine($"ACTION: {action.ActionType} - {action.Message}");
            }

            // Assert
            Assert.Equal(Nation.China, updatedGame.CurrentTurnNation); // The turn advanced to China
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
                mockServiceProvider.Setup(sp => sp.GetService(typeof(Imperial2030.Server.Services.INotificationService))).Returns(new Moq.Mock<Imperial2030.Server.Services.INotificationService>().Object);
                scope.Setup(s => s.ServiceProvider).Returns(mockServiceProvider.Object);
                return scope.Object;
            });

            var mockLogger = new Mock<ILogger<BotService>>();

            var botService = new BotService(mockScopeFactory.Object, mockHub.Object, [new Imperial2030.Server.Services.Bots.Strategies.DefaultBotStrategy()], mockLogger.Object) { SkipDelays = true };

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
            sp.Setup(sp => sp.GetService(typeof(Imperial2030.Server.Services.INotificationService))).Returns(new Moq.Mock<Imperial2030.Server.Services.INotificationService>().Object);

            var mockLogger = new Mock<ILogger<BotService>>();

            var botService = new BotService(scopeFactory.Object, mockHub.Object, [new Imperial2030.Server.Services.Bots.Strategies.DefaultBotStrategy()], mockLogger.Object)
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

        [Theory]
        [InlineData("RL")]
        [InlineData("RL-2")]
        public async Task TestRLBotWinRate(string testBotType)
        {
            int rlWins = 0;
            int totalGames = 50;
            for (int g = 0; g < totalGames; g++)
            {
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
                    mockServiceProvider.Setup(sp => sp.GetService(typeof(Imperial2030.Server.Services.INotificationService))).Returns(new Moq.Mock<Imperial2030.Server.Services.INotificationService>().Object);
                    scope.Setup(s => s.ServiceProvider).Returns(mockServiceProvider.Object);
                    return scope.Object;
                });

                var mockLogger = new Mock<ILogger<BotService>>();

                var botService = new BotService(mockScopeFactory.Object, mockHub.Object, [
                    new Imperial2030.Server.Services.Bots.Strategies.RandomBotStrategy(),
                    new Imperial2030.Server.Services.Bots.Strategies.DefaultBotStrategy(),
                    new Imperial2030.Server.Services.Bots.Strategies.GreedyBotStrategy(),
                    new Imperial2030.Server.Services.Bots.Strategies.AggressiveBotStrategy(),
                    new Imperial2030.Server.Services.Bots.Strategies.FriendlyBotStrategy(),
                    new Imperial2030.Server.Services.Bots.Strategies.RLBotStrategy(testBotType)
                ], mockLogger.Object);
                botService.SkipDelays = true;

                var store = new Mock<IUserStore<ApplicationUser>>();
                var mockUserManager = new Mock<UserManager<ApplicationUser>>(store.Object, null, null, null, null, null, null, null, null);
                var mockPresenceTracker = new Mock<PresenceTracker>();

                var gamesController = new GamesController(context, mockUserManager.Object, mockHub.Object, mockPresenceTracker.Object, botService, new Moq.Mock<Imperial2030.Server.Services.INotificationService>().Object);

                var userId = "host-user-id";
                var httpContext = new DefaultHttpContext();
                var claims = new List<Claim> { new Claim(ClaimTypes.NameIdentifier, userId) };
                var identity = new ClaimsIdentity(claims, "TestAuthType");
                httpContext.User = new ClaimsPrincipal(identity);

                gamesController.ControllerContext = new ControllerContext(new ActionContext(httpContext, new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor()));

                var createReq = new CreateGameRequest { Name = $"RLTestGame_{g}", MaxPlayers = 6, IsPrivate = false };
                var createRes = await gamesController.CreateGame(createReq);
                var gameId = Assert.IsType<GameDto>(Assert.IsType<CreatedAtActionResult>(createRes.Result).Value).Id;

                for (int i = 0; i < 5; i++)
                {
                    await gamesController.AddBot(gameId);
                }

                // Force them to be Random / RL bots
                var players = context.Players.Where(p => p.GameId == gameId).ToList();
                players[0].IsBot = true;
                players[0].BotName = $"{testBotType} Bot";
                players[0].BotType = testBotType;

                var randomOpponents = new[] { "Random", "Default" };//, "Greedy", "Aggressive", "Friendly" };
                var rng = new Random(g); // Use g for seed or just new Random()
                for (int i = 1; i < 6; i++)
                {
                    players[i].IsBot = true;
                    var opponentType = randomOpponents[rng.Next(randomOpponents.Length)];
                    players[i].BotName = $"{opponentType} Bot {i}";
                    players[i].BotType = opponentType;
                }
                await context.SaveChangesAsync();

                var startRes = await gamesController.StartGame(gameId);
                Assert.IsAssignableFrom<OkResult>(startRes);

                // The RL model is now correctly polled on state changes, so it should play out efficiently.
                int timeoutTicks = 0;
                while (timeoutTicks < 2000) // 100 * 50ms = 5 seconds timeout per game
                {
                    using var scope = mockScopeFactory.Object.CreateScope();
                    var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var game = ctx.Games.AsNoTracking().FirstOrDefault(g => g.Id == gameId);
                    if (game == null || game.Status == GameStatus.Finished) break;

                    await Task.Delay(50);
                    timeoutTicks++;
                }

                if (timeoutTicks >= 2000)
                {
                    var game = context.Games.AsNoTracking().Include(g => g.NationStates).FirstOrDefault(g => g.Id == gameId);
                    _output.WriteLine($"Game {g} hit timeout! IsInvestorTurn={game?.IsInvestorTurn}, ActingPlayerId={game?.ActingPlayerId}, CurrentNation={game?.CurrentTurnNation}");
                }

                // Count the number of moves played by the bots to get an accurate "turns" metric
                using var metricScope = mockScopeFactory.Object.CreateScope();
                var metricCtx = metricScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                int turns = metricCtx.GameActions.Count(a => a.GameId == gameId);

                var finalGame = context.Games.AsNoTracking().Include(g => g.Players).ThenInclude(p => p.Bonds).Include(g => g.NationStates).FirstOrDefault(g => g.Id == gameId);
                if (finalGame != null && finalGame.Status == GameStatus.Finished)
                {
                    var rankedPlayers = finalGame.GetRankedPlayers();
                    var winner = rankedPlayers.First();
                    if (winner.BotType == testBotType) rlWins++;
                    _output.WriteLine($"Game {g} finished in {turns} turns. Winner: {winner.BotName}");
                }
            }

            _output.WriteLine($"Total Actions Queried to Python Server: {Imperial2030.Server.Services.Bots.Strategies.RLBotStrategy.TotalActionCount}");
            _output.WriteLine($"Invalid Actions (Ignored/Randomized): {Imperial2030.Server.Services.Bots.Strategies.RLBotStrategy.InvalidActionCount}");
            if (Imperial2030.Server.Services.Bots.Strategies.RLBotStrategy.TotalActionCount > 0)
            {
                _output.WriteLine($"Invalid Action Rate: {Math.Round((double)Imperial2030.Server.Services.Bots.Strategies.RLBotStrategy.InvalidActionCount / Imperial2030.Server.Services.Bots.Strategies.RLBotStrategy.TotalActionCount * 100, 2)}%");
            }

            float winRate = (float)rlWins / totalGames * 100;
            _output.WriteLine($"{testBotType} Bot Win Rate: {rlWins}/{totalGames} ({winRate}%)");
            Assert.True(winRate >= 25);
        }

        [Fact]
        public async Task TestDynamicTerritoryControlUpdate()
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
                mockServiceProvider.Setup(sp => sp.GetService(typeof(Imperial2030.Server.Services.INotificationService))).Returns(new Moq.Mock<Imperial2030.Server.Services.INotificationService>().Object);
                scope.Setup(s => s.ServiceProvider).Returns(mockServiceProvider.Object);
                return scope.Object;
            });

            var mockLogger = new Mock<ILogger<BotService>>();

            var botService = new BotService(mockScopeFactory.Object, mockHub.Object, [new Imperial2030.Server.Services.Bots.Strategies.DefaultBotStrategy()], mockLogger.Object) { SkipDelays = true };

            var maneuverController = new ManeuverController(context, mockHub.Object, botService);

            var userId = "test-user-id";
            var httpContext = new DefaultHttpContext();
            var claims = new List<Claim> { new Claim(ClaimTypes.NameIdentifier, userId) };
            var identity = new ClaimsIdentity(claims, "TestAuthType");
            httpContext.User = new ClaimsPrincipal(identity);
            maneuverController.ControllerContext = new ControllerContext(new ActionContext(httpContext, new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor()));

            var gameId = Guid.NewGuid();
            var player = new Player { Id = Guid.NewGuid(), UserId = userId, IsHost = true };
            var game = new Game
            {
                Id = gameId,
                Name = "Test Game",
                Status = GameStatus.InProgress,
                CurrentTurnNation = Nation.China,
                CurrentManeuverPhase = ManeuverPhase.Armies,
                Players = new List<Player> { player },
                NationStates = new List<NationState>
                {
                    new NationState { Nation = Nation.China, ControllerId = player.Id },
                    new NationState { Nation = Nation.Europe, ControllerId = player.Id }
                }
            };
            context.Games.Add(game);

            var chinaArmy = new Unit { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.China, UnitType = UnitType.Army, TerritoryId = "NearEast" };
            var euArmy = new Unit { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.Europe, UnitType = UnitType.Army, TerritoryId = "NearEast" };
            context.Units.Add(chinaArmy);
            context.Units.Add(euArmy);

            // Set initial territory state (China flag)
            var ts = new TerritoryState { GameId = gameId, TerritoryId = "NearEast", Controller = Nation.China };
            context.TerritoryStates.Add(ts);

            await context.SaveChangesAsync();

            // Act: Move China Army OUT of Near East
            var req = new MoveUnitRequest { UnitId = chinaArmy.Id, DestinationId = "Turkey" };
            var result = await maneuverController.MoveArmy(gameId, req);

            Assert.IsType<OkResult>(result);

            // Assert: Near East flag should immediately become Europe
            var updatedTs = await context.TerritoryStates.FirstOrDefaultAsync(t => t.TerritoryId == "NearEast");
            Assert.NotNull(updatedTs);
            Assert.Equal(Nation.Europe, updatedTs.Controller);
        }

        [Fact]
        public async Task TestTerritoryControlUpdateWhenMovingIntoEmptyFlaggedTerritory()
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
                mockServiceProvider.Setup(sp => sp.GetService(typeof(Imperial2030.Server.Services.INotificationService))).Returns(new Moq.Mock<Imperial2030.Server.Services.INotificationService>().Object);
                scope.Setup(s => s.ServiceProvider).Returns(mockServiceProvider.Object);
                return scope.Object;
            });

            var mockLogger = new Mock<ILogger<BotService>>();

            var botService = new BotService(mockScopeFactory.Object, mockHub.Object, [new Imperial2030.Server.Services.Bots.Strategies.DefaultBotStrategy()], mockLogger.Object) { SkipDelays = true };

            var maneuverController = new ManeuverController(context, mockHub.Object, botService);

            var userId = "test-user-id";
            var httpContext = new DefaultHttpContext();
            var claims = new List<Claim> { new Claim(ClaimTypes.NameIdentifier, userId) };
            var identity = new ClaimsIdentity(claims, "TestAuthType");
            httpContext.User = new ClaimsPrincipal(identity);
            maneuverController.ControllerContext = new ControllerContext(new ActionContext(httpContext, new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor()));

            var gameId = Guid.NewGuid();
            var player = new Player { Id = Guid.NewGuid(), UserId = userId, IsHost = true };
            var game = new Game
            {
                Id = gameId,
                Name = "Test Game",
                Status = GameStatus.InProgress,
                CurrentTurnNation = Nation.Europe, // Europe's turn
                CurrentManeuverPhase = ManeuverPhase.Armies,
                Players = new List<Player> { player },
                NationStates = new List<NationState>
                {
                    new NationState { Nation = Nation.China, ControllerId = player.Id },
                    new NationState { Nation = Nation.Europe, ControllerId = player.Id }
                }
            };
            context.Games.Add(game);

            // EU Army is in Turkey, and will move to NearEast
            var euArmy = new Unit { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.Europe, UnitType = UnitType.Army, TerritoryId = "Turkey" };
            context.Units.Add(euArmy);

            // Set initial territory state for NearEast (China flag, NO armies)
            var ts = new TerritoryState { GameId = gameId, TerritoryId = "NearEast", Controller = Nation.China };
            context.TerritoryStates.Add(ts);

            await context.SaveChangesAsync();

            // Act: Move EU Army from Turkey INTO Near East
            var req = new MoveUnitRequest { UnitId = euArmy.Id, DestinationId = "NearEast", IsHostile = false };
            var result = await maneuverController.MoveArmy(gameId, req);

            Assert.IsType<OkResult>(result);

            // Assert: Near East flag should immediately become Europe
            var updatedTs = await context.TerritoryStates.FirstOrDefaultAsync(t => t.TerritoryId == "NearEast");
            Assert.NotNull(updatedTs);
            Assert.Equal(Nation.Europe, updatedTs.Controller);
        }

        [Fact]
        public async Task Max15Flags_Bot_FlagRemovedWithoutReplacement()
        {
            var dbName = Guid.NewGuid().ToString();
            using var context = GetDbContext(dbName);

            var gameId = Guid.NewGuid();
            var userId = Guid.NewGuid().ToString();
            var player = new Player { Id = Guid.NewGuid(), UserId = userId, IsHost = true };
            var gameObj = new Game
            {
                Id = gameId,
                Name = "Test Game",
                Status = GameStatus.InProgress,
                CurrentTurnNation = Nation.Russia,
                Players = new List<Player> { player },
                NationStates = new List<NationState>
                {
                    new NationState { Nation = Nation.Russia, ControllerId = player.Id }
                }
            };
            context.Games.Add(gameObj);

            for (int i = 0; i < 15; i++)
            {
                context.TerritoryStates.Add(new TerritoryState 
                { 
                    TerritoryId = $"T{i}", 
                    GameId = gameId, 
                    Controller = Nation.Russia 
                });
            }

            var targetTerritoryId = "Colombia";
            context.TerritoryStates.Add(new TerritoryState 
            { 
                TerritoryId = targetTerritoryId, 
                GameId = gameId, 
                Controller = Nation.Europe 
            });

            var army1 = new Unit { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.Russia, UnitType = UnitType.Army, TerritoryId = targetTerritoryId };
            context.Units.Add(army1);
            await context.SaveChangesAsync();

            var game = await context.Games
                .Include(g => g.TerritoryStates)
                .Include(g => g.Units)
                .Include(g => g.NationStates)
                .Include(g => g.Players)
                .FirstAsync(g => g.Id == gameId);

            var botService = new Imperial2030.Server.Services.BotService(
                new Mock<IServiceScopeFactory>().Object, 
                new Mock<IHubContext<Imperial2030.Server.Hubs.GameHub>>().Object, 
                new List<Imperial2030.Server.Services.Bots.IBotStrategy>(), 
                new Mock<ILogger<Imperial2030.Server.Services.BotService>>().Object);

            var methodInfo = typeof(Imperial2030.Server.Services.BotService).GetMethod("BotUpdateTerritoryControl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var task = (Task)methodInfo!.Invoke(botService, new object[] { context, game, "Bot" })!;
            await task;

            var targetState = context.TerritoryStates.FirstOrDefault(ts => ts.TerritoryId == targetTerritoryId);
            Assert.NotNull(targetState);
            Assert.Null(targetState.Controller);
            Assert.Equal(15, context.TerritoryStates.Count(ts => ts.Controller == Nation.Russia));
        }
    }
}
