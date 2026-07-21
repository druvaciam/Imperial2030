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

namespace Imperial2030.Tests
{
    public class GamesControllerTests
    {
        private ApplicationDbContext GetDbContext(string dbName)
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(databaseName: dbName)
                .Options;
            return new ApplicationDbContext(options);
        }

        private GamesController GetController(ApplicationDbContext context, string userId)
        {
            var mockHub = new Mock<IHubContext<Imperial2030.Server.Hubs.GameHub>>();
            var mockClients = new Mock<IHubClients>();
            var mockClientProxy = new Mock<IClientProxy>();

            mockHub.Setup(h => h.Clients).Returns(mockClients.Object);
            mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(mockClientProxy.Object);
            mockClients.Setup(c => c.All).Returns(mockClientProxy.Object);

            var mockUserManager = new Mock<UserManager<ApplicationUser>>(
                new Mock<IUserStore<ApplicationUser>>().Object,
                null, null, null, null, null, null, null, null);

            var presenceTracker = new PresenceTracker();

            var mockScopeFactory = new Mock<IServiceScopeFactory>();
            var mockServiceProvider = new Mock<IServiceProvider>();
            mockScopeFactory.Setup(s => s.CreateScope()).Returns(() =>
            {
                var scope = new Mock<IServiceScope>();
                scope.Setup(s => s.ServiceProvider).Returns(mockServiceProvider.Object);
                return scope.Object;
            });
            var botService = new BotService(mockScopeFactory.Object, mockHub.Object);

            var controller = new GamesController(context, mockUserManager.Object, mockHub.Object, presenceTracker, botService);

            var claims = new List<Claim> { new Claim(ClaimTypes.NameIdentifier, userId) };
            var identity = new ClaimsIdentity(claims, "TestAuthType");
            var claimsPrincipal = new ClaimsPrincipal(identity);

            var httpContext = new DefaultHttpContext { User = claimsPrincipal };

            var routeData = new Microsoft.AspNetCore.Routing.RouteData();
            var actionDescriptor = new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor();

            var actionContext = new ActionContext(httpContext, routeData, actionDescriptor);
            controller.ControllerContext = new ControllerContext(actionContext);

            return controller;
        }

        private async Task<(Guid GameId, string UserId, Guid PlayerId)> SetupGame(ApplicationDbContext context, int rondelPosition)
        {
            var gameId = Guid.NewGuid();
            var userId = "test-user-id";
            var playerId = Guid.NewGuid();

            var game = new Game
            {
                Id = gameId,
                CurrentTurnNation = Nation.Russia,
                Status = GameStatus.InProgress
            };

            var player = new Player { Id = playerId, GameId = gameId, UserId = userId };

            var nsRussia = new NationState 
            { 
                Nation = Nation.Russia, 
                ControllerId = playerId, 
                GameId = gameId,
                RondelPosition = rondelPosition,
                Treasury = 0
            };
            var nsEurope = new NationState { Nation = Nation.Europe, ControllerId = Guid.NewGuid(), GameId = gameId };

            context.Games.Add(game);
            context.Players.Add(player);
            context.NationStates.AddRange(nsRussia, nsEurope);
            await context.SaveChangesAsync();

            return (gameId, userId, playerId);
        }

        [Fact]
        public async Task ExecuteProduction_OccupiedFactory_DoesNotProduce()
        {
            var dbName = Guid.NewGuid().ToString();
            using (var context = GetDbContext(dbName))
            {
                // RondelPosition 2 is Production
                var (gameId, userId, _) = await SetupGame(context, rondelPosition: 2);

                var moscowId = "Moscow"; // Russia's home city with a factory
                var vladivostokId = "Vladivostok"; // Russia's other home city

                var tMoscow = new TerritoryState { TerritoryId = moscowId, GameId = gameId, Controller = Nation.Russia, HasFactory = true };
                var tVladivostok = new TerritoryState { TerritoryId = vladivostokId, GameId = gameId, Controller = Nation.Russia, HasFactory = true };

                // Place a hostile Europe army in Moscow
                var hostileArmy = new Unit { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.Europe, UnitType = UnitType.Army, TerritoryId = moscowId, IsHostile = true };

                context.TerritoryStates.AddRange(tMoscow, tVladivostok);
                context.Units.Add(hostileArmy);
                await context.SaveChangesAsync();

                var controller = GetController(context, userId);

                // Act
                var result = await controller.ExecuteProduction(gameId);

                // Assert
                Assert.IsType<OkObjectResult>(result);

                var units = await context.Units.Where(u => u.GameId == gameId).ToListAsync();
                
                // One hostile army should remain
                Assert.Contains(units, u => u.Id == hostileArmy.Id);

                // Moscow is occupied, so it should NOT produce a unit
                Assert.DoesNotContain(units, u => u.Nation == Nation.Russia && u.TerritoryId == moscowId);

                // Vladivostok is NOT occupied, so it SHOULD produce an army (because it is a light brown city)
                // Wait, Vladivostok is a port city (LightBlue) so it produces a Fleet.
                Assert.Contains(units, u => u.Nation == Nation.Russia && u.TerritoryId == vladivostokId);
                
                // Total units should be 1 (hostile) + 1 (produced in Vladivostok) = 2
                Assert.Equal(2, units.Count);
            }
        }

        [Fact]
        public async Task ExecuteTaxation_OccupiedFactory_DoesNotYieldRevenue()
        {
            var dbName = Guid.NewGuid().ToString();
            using (var context = GetDbContext(dbName))
            {
                // RondelPosition 0 is Taxation
                var (gameId, userId, _) = await SetupGame(context, rondelPosition: 0);

                var moscowId = "Moscow"; // Russia's home city with a factory
                var vladivostokId = "Vladivostok"; // Russia's other home city

                var tMoscow = new TerritoryState { TerritoryId = moscowId, GameId = gameId, Controller = Nation.Russia, HasFactory = true };
                var tVladivostok = new TerritoryState { TerritoryId = vladivostokId, GameId = gameId, Controller = Nation.Russia, HasFactory = true };

                // Place a foreign army in Moscow (Taxation just checks u.Nation != nation for armies)
                var foreignArmy = new Unit { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.Europe, UnitType = UnitType.Army, TerritoryId = moscowId };

                context.TerritoryStates.AddRange(tMoscow, tVladivostok);
                context.Units.Add(foreignArmy);
                await context.SaveChangesAsync();

                var controller = GetController(context, userId);

                // Act
                var result = await controller.ExecuteTaxation(gameId);

                // Assert
                Assert.IsType<OkObjectResult>(result);

                var nationState = await context.NationStates.FirstAsync(n => n.GameId == gameId && n.Nation == Nation.Russia);

                // Revenue calculation:
                // Factories: Moscow is occupied (0M), Vladivostok is free (2M). Total = 2M.
                // Flags: Moscow and Vladivostok are controlled by Russia. Total = 2M.
                // Total Tax Revenue = 4M.
                // Soldiers' Pay: 0 units of Russia = 0M.
                // Net Treasury change = +4M.
                Assert.Equal(4, nationState.Treasury);
            }
        }
    }
}
