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

        /// <summary>
        /// A convoy is a last resort - it is for destinations nothing simpler reaches. Moving an army one
        /// step to an adjacent territory must not consume fleets, and must not be logged as a sea voyage.
        ///
        /// The bot used to decide by looking the destination up in its convoy-paths dictionary, which
        /// holds an entry for every destination a convoy COULD reach - adjacent ones included. So an army
        /// stepping from New Orleans to Mexico, two neighbouring territories, was shipped "via the North
        /// Pacific": the log claimed a voyage that never happened, and the carrying fleets were flagged
        /// HasConvoyed, spending carriers that could have moved another army that turn.
        /// </summary>
        [Fact]
        public async Task BotDoesNotConvoyToAnAdjacentTerritory()
        {
            string dbName = Guid.NewGuid().ToString();
            var ctx = GetDbContext(dbName);

            var gameId = Guid.NewGuid();
            var playerId = Guid.NewGuid();

            // New Orleans and Mexico are neighbours, and both also touch seas the USA has fleets in - so a
            // convoy route exists alongside the plain step, which is exactly the ambiguity that bit.
            Assert.Contains("Mexico", MapConnectivity.GetNeighbors("NewOrleans", isFleet: false));

            var game = new Game
            {
                Id = gameId,
                CurrentTurnNation = Nation.USA,
                CurrentManeuverPhase = ManeuverPhase.None,
                Status = GameStatus.InProgress,
                Players = new List<Player> { new Player { Id = playerId, IsBot = true, BotType = "StubMexico" } },
                NationStates = new List<NationState>
                {
                    new NationState { Nation = Nation.USA, ControllerId = playerId, Power = 10, Treasury = 10, RondelPosition = 2, HasMovedThisTurn = false }
                },
                Units = new List<Unit>
                {
                    new Unit { Nation = Nation.USA, UnitType = UnitType.Army, TerritoryId = "NewOrleans", HasMoved = false },
                    new Unit { Nation = Nation.USA, UnitType = UnitType.Fleet, TerritoryId = "CaribbeanSea", HasMoved = true },
                    new Unit { Nation = Nation.USA, UnitType = UnitType.Fleet, TerritoryId = "NorthPacific", HasMoved = true }
                }
            };

            ctx.Games.Add(game);
            await ctx.SaveChangesAsync();

            var mockHub = new Mock<IHubContext<Imperial2030.Server.Hubs.GameHub>>();
            var mockClients = new Mock<IHubClients>();
            mockHub.Setup(h => h.Clients).Returns(mockClients.Object);
            mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(new Mock<IClientProxy>().Object);

            var mockScopeFactory = new Mock<IServiceScopeFactory>();
            mockScopeFactory.Setup(s => s.CreateScope()).Returns(() =>
            {
                var scope = new Mock<IServiceScope>();
                var sp = new Mock<IServiceProvider>();
                sp.Setup(x => x.GetService(typeof(ApplicationDbContext))).Returns(GetDbContext(dbName));
                sp.Setup(x => x.GetService(typeof(INotificationService))).Returns(new Mock<INotificationService>().Object);
                scope.Setup(s => s.ServiceProvider).Returns(sp.Object);
                return scope.Object;
            });

            var botService = new BotService(mockScopeFactory.Object, mockHub.Object,
                [new StubMexicoBotStrategy()], new Mock<ILogger<BotService>>().Object);
            botService.SkipDelays = true;
            await botService.TryPlayBotTurnAsync(gameId, singleTurnOnly: true);

            var after = GetDbContext(dbName);
            var updatedGame = await after.Games.Include(g => g.Units).Include(g => g.Actions).FirstAsync(g => g.Id == gameId);

            Assert.Equal("Mexico", updatedGame.Units.First(u => u.UnitType == UnitType.Army).TerritoryId);

            // No fleet was needed, so none may have been spent.
            Assert.DoesNotContain(updatedGame.Units.Where(u => u.UnitType == UnitType.Fleet), f => f.HasConvoyed);

            // ...and the log must describe a step, not a voyage.
            var moveAction = updatedGame.Actions.Where(a => a.ActionType == "MoveArmy").OrderBy(a => a.OrderIndex).Last();
            var meta = System.Text.Json.JsonSerializer.Deserialize<ActionMetadata>(moveAction.Metadata,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.Equal("Mexico", meta!.ToTerritoryId);
            Assert.True(meta.RouteVia == null || meta.RouteVia.Count == 0,
                $"An adjacent step must log no waypoints, got: {string.Join(", ", meta.RouteVia ?? new List<string>())}");
        }
    }

    public class StubMexicoBotStrategy : BotStrategyBase
    {
        public override string Name => "StubMexico";
        public override double ScoreRondelSlot(int slot, Game game, NationState ns, Player controller, int factories, int units) => slot == 3 ? 100 : 0;
        public override double ScoreManeuverDestination(Game game, Unit unit, string destinationId, Player controller) => destinationId == "Mexico" ? 1000 : 0;
        public override bool DetermineHostility(bool hasEnemy, bool isForeignHome) => false;
        public override bool RetreatFromBattle(Game game, PendingBattle battle) => false;
    }
}
