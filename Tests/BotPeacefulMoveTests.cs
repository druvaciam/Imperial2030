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
    public class StubPeacefulBotStrategy : BotStrategyBase
    {
        public override string Name => "StubPeaceful";
        
        public override double ScoreRondelSlot(int slot, Game game, NationState ns, Player controller, int factories, int units)
        {
            return slot == 3 ? 100 : 0; // Always Maneuver
        }

        public override double ScoreManeuverDestination(Game game, Unit unit, string destinationId, Player controller)
        {
            return destinationId == "Ukraine" ? 1000 : 0;
        }
        
        public override bool DetermineHostility(bool hasEnemy, bool isForeignHome)
        {
            return false; // Always peaceful
        }
        
        public override bool RetreatFromBattle(Game game, PendingBattle battle)
        {
            return false;
        }
    }

    public class BotPeacefulMoveTests
    {
        private ApplicationDbContext GetDbContext(string dbName)
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(databaseName: dbName)
                .Options;
            return new ApplicationDbContext(options);
        }

        [Fact]
        public async Task TestBotPeacefulMoveTriggersPendingBattle()
        {
            string dbName = Guid.NewGuid().ToString();
            var ctx = GetDbContext(dbName);

            var gameId = Guid.NewGuid();
            var playerId = Guid.NewGuid();
            var defenderId = Guid.NewGuid();

            var game = new Game
            {
                Id = gameId,
                CurrentTurnNation = Nation.Europe,
                CurrentManeuverPhase = ManeuverPhase.None, // Hasn't started
                Status = GameStatus.InProgress,
                Players = new List<Player>
                {
                    new Player { Id = playerId, IsBot = true, BotType = "StubPeaceful" },
                    new Player { Id = defenderId, IsBot = true, BotType = "Default" }
                },
                NationStates = new List<NationState>
                {
                    new NationState { Nation = Nation.Europe, ControllerId = playerId, Power = 10, Treasury = 10, RondelPosition = 2, HasMovedThisTurn = false }, 
                    new NationState { Nation = Nation.Russia, ControllerId = defenderId }
                },
                Units = new List<Unit>
                {
                    new Unit { Nation = Nation.Europe, UnitType = UnitType.Army, TerritoryId = "Moscow", HasMoved = false },
                    new Unit { Nation = Nation.Russia, UnitType = UnitType.Army, TerritoryId = "Ukraine", HasMoved = false }
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
            var botService = new BotService(mockScopeFactory.Object, mockHub.Object, [new StubPeacefulBotStrategy()], loggerMock.Object);
            botService.SkipDelays = true;
            await botService.TryPlayBotTurnAsync(gameId, singleTurnOnly: true);

            var updatedGame = await GetDbContext(dbName).Games.Include(g => g.Units).FirstAsync(g => g.Id == gameId);
            
            // Verify army moved
            var ahArmy = updatedGame.Units.First(u => u.Nation == Nation.Europe);
            Assert.Equal("Ukraine", ahArmy.TerritoryId);

            // Verify negotiation phase was triggered
            Assert.Equal("Ukraine", updatedGame.PendingBattleTerritoryId);
            Assert.Equal(Nation.Europe, updatedGame.PendingBattleAggressorNation);
            Assert.Contains(Nation.Russia, updatedGame.PendingBattleDefenders);
        }
    }
}
