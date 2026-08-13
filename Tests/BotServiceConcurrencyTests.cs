using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Moq;
using Microsoft.EntityFrameworkCore;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using Imperial2030.Server.Services;
using Imperial2030.Server.Data;
using Imperial2030.Server.Services.Bots;
using Imperial2030.Server.Services.Bots.Strategies;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace Imperial2030.Tests
{
    public class StubProductionBotStrategy : BotStrategyBase
    {
        public override string Name => "StubProduction";
        
        public override double ScoreRondelSlot(int slot, Game game, NationState ns, Player controller, int factories, int units)
        {
            return slot == 2 ? 100 : 0; // Always choose Production (Slot 2)
        }

        public override bool RetreatFromBattle(Game game, PendingBattle battle)
        {
            return false;
        }
    }

    public class BotServiceConcurrencyTests
    {
        private ApplicationDbContext GetDbContext(string dbName)
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(databaseName: dbName)
                .Options;
            return new ApplicationDbContext(options);
        }

        [Fact]
        public async Task TestHostileArmyBlocksBotProduction_EFCoreStaleState()
        {
            string dbName = Guid.NewGuid().ToString();
            var ctx = GetDbContext(dbName);

            var gameId = Guid.NewGuid();
            var p1Id = Guid.NewGuid();
            var p2Id = Guid.NewGuid(); // P2 controls India AND China

            var game = new Game
            {
                Id = gameId,
                CurrentTurnNation = Nation.India,
                Status = GameStatus.InProgress,
                Players = new List<Player>
                {
                    new Player { Id = p1Id, IsBot = false },
                    new Player { Id = p2Id, IsBot = true, BotType = "StubProduction" }
                },
                NationStates = new List<NationState>
                {
                    new NationState { Nation = Nation.India, ControllerId = p2Id, Power = 10, Treasury = 10, RondelPosition = 1 }, 
                    new NationState { Nation = Nation.China, ControllerId = p2Id } // P2 bought a large China bond and took control from P1!
                },
                TerritoryStates = new List<TerritoryState>
                {
                    new TerritoryState { TerritoryId = "NewDelhi", GameId = gameId, HasFactory = true }
                },
                Units = new List<Unit>
                {
                    // Start as NOT hostile so BotService caches it as such
                    new Unit { Id = Guid.NewGuid(), Nation = Nation.China, UnitType = UnitType.Army, TerritoryId = "NewDelhi", IsHostile = false }
                }
            };

            ctx.Games.Add(game);
            await ctx.SaveChangesAsync();

            // Mock IHubContext
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

            var loggerMock = new Mock<ILogger<BotService>>();
            var botService = new BotService(mockScopeFactory.Object, mockHub.Object, new List<IBotStrategy> { new StubProductionBotStrategy() }, loggerMock.Object);
            
            botService.SkipDelays = false;

            var botTask = botService.TryPlayBotTurnAsync(gameId, singleTurnOnly: true);
            
            // Wait a moment for BotService to pull the initial state (where IsHostile = false)
            // BotDelayMs is 5000ms, so 2000ms is perfectly in the middle of the delay.
            await Task.Delay(2000);

            // While BotService is waiting on BotDelayMs, simulate another player making the unit hostile
            using (var concurrentCtx = GetDbContext(dbName))
            {
                var unit = await concurrentCtx.Units.FirstAsync();
                unit.IsHostile = true;
                await concurrentCtx.SaveChangesAsync();
            }

            // Wait for BotService to finish its turn
            await botTask;

            var updatedGame = await GetDbContext(dbName).Games.Include(g => g.Units).FirstAsync(g => g.Id == gameId);
            
            // Verify India did NOT produce in NewDelhi
            var indiaArmies = updatedGame.Units.Count(u => u.Nation == Nation.India && u.TerritoryId == "NewDelhi");
            Assert.Equal(0, indiaArmies); // Expected: 0, because China IS blocking it now.
        }
    }
}
