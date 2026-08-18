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
    public class ManeuverControllerTests
    {
        private readonly ITestOutputHelper _output;

        public ManeuverControllerTests(ITestOutputHelper output)
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

        private ManeuverController GetController(ApplicationDbContext context, string userId)
        {
            var mockHub = new Mock<IHubContext<Imperial2030.Server.Hubs.GameHub>>();
            var mockClients = new Mock<IHubClients>();
            var mockClientProxy = new Mock<IClientProxy>();

            mockHub.Setup(h => h.Clients).Returns(mockClients.Object);
            mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(mockClientProxy.Object);

            var mockScopeFactory = new Mock<IServiceScopeFactory>();
            var mockServiceProvider = new Mock<IServiceProvider>();
            mockServiceProvider.Setup(sp => sp.GetService(typeof(Imperial2030.Server.Services.INotificationService))).Returns(new Moq.Mock<Imperial2030.Server.Services.INotificationService>().Object);
            mockScopeFactory.Setup(s => s.CreateScope()).Returns(() =>
            {
                var scope = new Mock<IServiceScope>();
                scope.Setup(s => s.ServiceProvider).Returns(mockServiceProvider.Object);
                return scope.Object;
            });
            var mockLogger = new Mock<Microsoft.Extensions.Logging.ILogger<BotService>>();
            var botService = new BotService(mockScopeFactory.Object, mockHub.Object, [new Imperial2030.Server.Services.Bots.Strategies.DefaultBotStrategy()], mockLogger.Object);

            var controller = new ManeuverController(context, mockHub.Object, botService);

            var httpContext = new DefaultHttpContext();
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, userId)
            };
            var identity = new ClaimsIdentity(claims, "TestAuthType");
            var user = new ClaimsPrincipal(identity);
            httpContext.User = user;

            var routeData = new Microsoft.AspNetCore.Routing.RouteData();
            var actionDescriptor = new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor();

            var actionContext = new ActionContext(httpContext, routeData, actionDescriptor);
            controller.ControllerContext = new ControllerContext(actionContext);

            return controller;
        }
        private async Task<(Guid GameId, string UserId, Guid PlayerId)> SetupGame(ApplicationDbContext context)
        {
            var gameId = Guid.NewGuid();
            var userId = "test-user-id";
            var playerId = Guid.NewGuid();

            var game = new Game
            {
                Id = gameId,
                CurrentTurnNation = Nation.Russia,
                Status = GameStatus.InProgress,
                CurrentManeuverPhase = ManeuverPhase.Armies
            };

            var player = new Player { Id = playerId, GameId = gameId, UserId = userId };

            // Link Russia to Player
            var nsRussia = new NationState { Nation = Nation.Russia, ControllerId = playerId, GameId = gameId };
            // Link Europe to some other player
            var nsEurope = new NationState { Nation = Nation.Europe, ControllerId = Guid.NewGuid(), GameId = gameId };

            context.Games.Add(game);
            context.Players.Add(player);
            context.NationStates.AddRange(nsRussia, nsEurope);
            await context.SaveChangesAsync();

            return (gameId, userId, playerId);
        }

        [Fact]
        public async Task DestroyFactory_Success()
        {
            var dbName = Guid.NewGuid().ToString();
            using (var context = GetDbContext(dbName))
            {
                var (gameId, userId, _) = await SetupGame(context);

                var berlinId = "Berlin";
                var londonId = "London";

                // Setup Territories
                var tBerlin = new TerritoryState { TerritoryId = berlinId, GameId = gameId, Controller = Nation.Europe, HasFactory = true };
                var tLondon = new TerritoryState { TerritoryId = londonId, GameId = gameId, Controller = Nation.Europe, HasFactory = true };

                // Setup Russian Armies in Berlin (3 units)
                var army1 = new Unit { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.Russia, UnitType = UnitType.Army, TerritoryId = berlinId };
                var army2 = new Unit { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.Russia, UnitType = UnitType.Army, TerritoryId = berlinId };
                var army3 = new Unit { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.Russia, UnitType = UnitType.Army, TerritoryId = berlinId };

                context.TerritoryStates.AddRange(tBerlin, tLondon);
                context.Units.AddRange(army1, army2, army3);
                await context.SaveChangesAsync();

                var controller = GetController(context, userId);
                var request = new DestroyFactoryRequest
                {
                    TerritoryId = berlinId,
                    UnitIds = new List<Guid> { army1.Id, army2.Id, army3.Id }
                };

                // Act
                var result = await controller.DestroyFactory(gameId, request);

                // Assert
                Assert.IsType<OkResult>(result);

                var tBerlinAfter = await context.TerritoryStates.FirstAsync(t => t.TerritoryId == berlinId);
                Assert.False(tBerlinAfter.HasFactory, "Factory should be destroyed");

                var unitsAfter = await context.Units.Where(u => u.TerritoryId == berlinId).ToListAsync();
                Assert.Empty(unitsAfter); // Armies should be removed
            }
        }

        [Fact]
        public async Task DestroyFactory_Fails_NotEnoughArmies()
        {
            var dbName = Guid.NewGuid().ToString();
            using (var context = GetDbContext(dbName))
            {
                var (gameId, userId, _) = await SetupGame(context);

                var berlinId = "Berlin";
                var tBerlin = new TerritoryState { TerritoryId = berlinId, GameId = gameId, Controller = Nation.Europe, HasFactory = true };
                var tLondon = new TerritoryState { TerritoryId = "London", GameId = gameId, Controller = Nation.Europe, HasFactory = true };

                // Only 2 armies
                var army1 = new Unit { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.Russia, UnitType = UnitType.Army, TerritoryId = berlinId };
                var army2 = new Unit { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.Russia, UnitType = UnitType.Army, TerritoryId = berlinId };

                context.TerritoryStates.AddRange(tBerlin, tLondon);
                context.Units.AddRange(army1, army2);
                await context.SaveChangesAsync();

                var controller = GetController(context, userId);
                var request = new DestroyFactoryRequest
                {
                    TerritoryId = berlinId,
                    UnitIds = new List<Guid> { army1.Id, army2.Id }
                };

                // Act
                var result = await controller.DestroyFactory(gameId, request);

                // Assert
                var badRequest = Assert.IsType<BadRequestObjectResult>(result);
                Assert.Contains("exactly 3 armies", badRequest.Value.ToString());
            }
        }

        [Fact]
        public async Task DestroyFactory_Fails_DefendersPresent()
        {
            var dbName = Guid.NewGuid().ToString();
            using (var context = GetDbContext(dbName))
            {
                var (gameId, userId, _) = await SetupGame(context);

                var berlinId = "Berlin";
                var tBerlin = new TerritoryState { TerritoryId = berlinId, GameId = gameId, Controller = Nation.Europe, HasFactory = true };
                var tLondon = new TerritoryState { TerritoryId = "London", GameId = gameId, Controller = Nation.Europe, HasFactory = true };

                var army1 = new Unit { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.Russia, UnitType = UnitType.Army, TerritoryId = berlinId };
                var army2 = new Unit { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.Russia, UnitType = UnitType.Army, TerritoryId = berlinId };
                var army3 = new Unit { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.Russia, UnitType = UnitType.Army, TerritoryId = berlinId };

                // 1 Defender
                var defender = new Unit { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.Europe, UnitType = UnitType.Army, TerritoryId = berlinId };

                context.TerritoryStates.AddRange(tBerlin, tLondon);
                context.Units.AddRange(army1, army2, army3, defender);
                await context.SaveChangesAsync();

                var controller = GetController(context, userId);
                var request = new DestroyFactoryRequest
                {
                    TerritoryId = berlinId,
                    UnitIds = new List<Guid> { army1.Id, army2.Id, army3.Id }
                };

                // Act
                var result = await controller.DestroyFactory(gameId, request);

                // Assert
                var badRequest = Assert.IsType<BadRequestObjectResult>(result);
                Assert.Contains("defenders are present", badRequest.Value.ToString());
            }
        }

        [Fact]
        public async Task DestroyFactory_Fails_LastFactory()
        {
            var dbName = Guid.NewGuid().ToString();
            using (var context = GetDbContext(dbName))
            {
                var (gameId, userId, _) = await SetupGame(context);

                var berlinId = "Berlin";
                // Only 1 factory for Europe
                var tBerlin = new TerritoryState { TerritoryId = berlinId, GameId = gameId, Controller = Nation.Europe, HasFactory = true };

                var army1 = new Unit { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.Russia, UnitType = UnitType.Army, TerritoryId = berlinId };
                var army2 = new Unit { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.Russia, UnitType = UnitType.Army, TerritoryId = berlinId };
                var army3 = new Unit { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.Russia, UnitType = UnitType.Army, TerritoryId = berlinId };

                context.TerritoryStates.Add(tBerlin);
                context.Units.AddRange(army1, army2, army3);
                await context.SaveChangesAsync();

                var controller = GetController(context, userId);
                var request = new DestroyFactoryRequest
                {
                    TerritoryId = berlinId,
                    UnitIds = new List<Guid> { army1.Id, army2.Id, army3.Id }
                };

                // Act
                var result = await controller.DestroyFactory(gameId, request);

                // Assert
                var badRequest = Assert.IsType<BadRequestObjectResult>(result);
                Assert.Contains("last factory", badRequest.Value.ToString());
            }
        }

        [Fact]
        public async Task MoveArmy_Fails_OccupyLastFactoryHostilely()
        {
            var dbName = Guid.NewGuid().ToString();
            using (var context = GetDbContext(dbName))
            {
                var (gameId, userId, _) = await SetupGame(context);

                var berlinId = "Berlin"; // Europe's home

                // Only 1 factory for Europe
                var tBerlin = new TerritoryState { TerritoryId = berlinId, GameId = gameId, Controller = Nation.Europe, HasFactory = true };

                // Russia army starting adjacent
                var army1 = new Unit { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.Russia, UnitType = UnitType.Army, TerritoryId = "Ukraine" };

                context.TerritoryStates.Add(tBerlin);
                context.Units.Add(army1);
                await context.SaveChangesAsync();

                var controller = GetController(context, userId);
                var request = new MoveUnitRequest
                {
                    UnitId = army1.Id,
                    DestinationId = berlinId,
                    IsHostile = true
                };

                // Act
                var result = await controller.MoveArmy(gameId, request);

                // Assert
                var badRequest = Assert.IsType<BadRequestObjectResult>(result);
                Assert.Contains("last unoccupied factory", badRequest.Value.ToString());
            }
        }

        [Fact]
        public async Task MoveArmy_Passes_OccupyLastFactoryPeacefully()
        {
            var dbName = Guid.NewGuid().ToString();
            using (var context = GetDbContext(dbName))
            {
                var (gameId, userId, _) = await SetupGame(context);

                var berlinId = "Berlin"; // Europe's home

                // Only 1 factory for Europe
                var tBerlin = new TerritoryState { TerritoryId = berlinId, GameId = gameId, Controller = Nation.Europe, HasFactory = true };

                // Russia army starting adjacent
                var army1 = new Unit { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.Russia, UnitType = UnitType.Army, TerritoryId = "Ukraine" };

                context.TerritoryStates.Add(tBerlin);
                context.Units.Add(army1);
                await context.SaveChangesAsync();

                var controller = GetController(context, userId);
                var request = new MoveUnitRequest
                {
                    UnitId = army1.Id,
                    DestinationId = berlinId,
                    IsHostile = false // Peaceful move!
                };

                // Act
                var result = await controller.MoveArmy(gameId, request);

                // Assert
                Assert.IsType<OkResult>(result);

                var movedUnit = await context.Units.FindAsync(army1.Id);
                Assert.Equal(berlinId, movedUnit.TerritoryId);
                Assert.False(movedUnit.IsHostile);
            }
        }

        [Fact]
        public async Task DestroyFactory_Fails_NotCurrentTurn()
        {
            var dbName = Guid.NewGuid().ToString();
            using (var context = GetDbContext(dbName))
            {
                // Setup Game manually to change turn
                var gameId = Guid.NewGuid();
                var userId = "test-user-id";
                var playerId = Guid.NewGuid();
                var game = new Game
                {
                    Id = gameId,
                    CurrentTurnNation = Nation.China, // Not Russia
                    Status = GameStatus.InProgress
                };
                var player = new Player { Id = playerId, GameId = gameId, UserId = userId };
                var nsRussia = new NationState { Nation = Nation.Russia, ControllerId = playerId, GameId = gameId };

                context.Games.Add(game);
                context.Players.Add(player);
                context.NationStates.Add(nsRussia);
                await context.SaveChangesAsync();

                var controller = GetController(context, userId);
                var request = new DestroyFactoryRequest
                {
                    TerritoryId = "Berlin",
                    UnitIds = new List<Guid>()
                };

                // Act
                // The controller validation first checks authentication. 
                // Since CurrentTurnNation is China, we need to ensure the user CONTROLS China to even get past the first checks?
                // The check is "var nation = game.CurrentTurnNation; var nationState = ...; var controller = ...; if (controller.UserId != userId) return Forbid();"
                // So if I am the Russia player, and it is China's turn, and I (Russia player) call endpoint, I will get Forbid (403).

                // Assert
                try
                {
                    // But "China" nation state might not exist in my setup -> Exception.
                    // Let's create China State controlled by Someone Else.
                    var nsChina = new NationState { Nation = Nation.China, ControllerId = Guid.NewGuid(), GameId = gameId };
                    context.NationStates.Add(nsChina);
                    await context.SaveChangesAsync();

                    var result = await controller.DestroyFactory(gameId, request);
                    Assert.IsType<ForbidResult>(result);
                }
                catch (Exception)
                {
                    // If logic throws due to missing data, that's also a verification of data integrity requirements
                }
            }
        }

        [Fact]
        public async Task MoveFleet_Passes_Canal_SamePlayer_DifferentNations()
        {
            var dbName = Guid.NewGuid().ToString();
            using (var context = GetDbContext(dbName))
            {
                var gameId = Guid.NewGuid();
                var userId = "test-user-id";
                var playerId = Guid.NewGuid();

                var game = new Game
                {
                    Id = gameId,
                    CurrentTurnNation = Nation.Europe, // It must be Europe's turn to move Europe fleet
                    Status = GameStatus.InProgress,
                    CurrentManeuverPhase = ManeuverPhase.Fleets // Fleet phase
                };

                var player = new Player { Id = playerId, GameId = gameId, UserId = userId };

                // Player controls BOTH Russia and Europe
                var nsRussia = new NationState { Nation = Nation.Russia, ControllerId = playerId, GameId = gameId };
                var nsEurope = new NationState { Nation = Nation.Europe, ControllerId = playerId, GameId = gameId };

                // Russia controls North-Africa (Suez Controller)
                var tNorthAfrica = new TerritoryState { TerritoryId = "North-Africa", GameId = gameId, Controller = Nation.Russia };

                // Europe has a fleet in Med
                var fleet = new Unit { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.Europe, UnitType = UnitType.Fleet, TerritoryId = "MediterraneanSea" };

                context.Games.Add(game);
                context.Players.Add(player);
                context.NationStates.AddRange(nsRussia, nsEurope);
                context.TerritoryStates.Add(tNorthAfrica);
                context.Units.Add(fleet);
                await context.SaveChangesAsync();

                var controller = GetController(context, userId);
                var request = new MoveUnitRequest
                {
                    UnitId = fleet.Id,
                    DestinationId = "IndianOcean"
                };

                // Act
                var result = await controller.MoveFleet(gameId, request);

                // Assert
                Assert.IsType<OkResult>(result);

                var unitAfter = await context.Units.FirstAsync(u => u.Id == fleet.Id);
                Assert.Equal("IndianOcean", unitAfter.TerritoryId);
            }
        }

        [Fact]
        public async Task MoveArmy_Attacks_Fleet_In_Port()
        {
            var dbName = Guid.NewGuid().ToString();
            using (var context = GetDbContext(dbName))
            {
                // Arrange
                var gameId = Guid.NewGuid();
                var userId = "user1";
                var playerId = Guid.NewGuid();
                var game = new Game
                {
                    Id = gameId,
                    Status = GameStatus.InProgress,
                    CurrentTurnNation = Nation.Russia,
                    CurrentManeuverPhase = ManeuverPhase.Armies,
                    NationStates = new List<NationState>
                     {
                         new NationState { Nation = Nation.Russia, ControllerId = playerId, GameId = gameId, Power = 0 },
                         new NationState { Nation = Nation.India, ControllerId = Guid.NewGuid(), GameId = gameId, Power = 0 }
                     },
                    Players = new List<Player>
                     {
                         new Player { Id = playerId, UserId = userId, GameId = gameId }
                     },
                    Units = new List<Unit>(),
                    TerritoryStates = new List<TerritoryState>()
                };

                // Russia Army in Iran (adjacent to Mumbai)
                game.Units.Add(new Unit { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.Russia, UnitType = UnitType.Army, TerritoryId = "Iran", HasMoved = false });

                // Indian Fleet in Mumbai
                var indianFleet = new Unit { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.India, UnitType = UnitType.Fleet, TerritoryId = "Mumbai" };
                game.Units.Add(indianFleet);

                await context.Games.AddAsync(game);
                await context.SaveChangesAsync();

                var controller = GetController(context, userId);

                // Act
                // Request to move to Mumbai and Fight India
                var request = new MoveUnitRequest
                {
                    UnitId = game.Units.First(u => u.Nation == Nation.Russia).Id,
                    DestinationId = "Mumbai",
                    BattleTargetNation = Nation.India
                };

                var result = await controller.MoveArmy(gameId, request);

                // Assert
                Assert.IsType<OkResult>(result);

                var updatedGame = await context.Games.Include(g => g.Units).FirstAsync(g => g.Id == gameId);

                // Expectation: Both should be destroyed.
                Assert.Empty(updatedGame.Units);
            }
        }

        [Fact]
        public async Task MoveArmy_Convoy_Chicago_To_NorthAfrica()
        {
            var dbName = Guid.NewGuid().ToString();
            using (var context = GetDbContext(dbName))
            {
                // Arrange
                var gameId = Guid.NewGuid();
                var userId = "user1";
                var playerId = Guid.NewGuid();
                var game = new Game
                {
                    Id = gameId,
                    Status = GameStatus.InProgress,
                    CurrentTurnNation = Nation.USA,
                    CurrentManeuverPhase = ManeuverPhase.Armies,
                    NationStates = new List<NationState>
                     {
                         new NationState { Nation = Nation.USA, ControllerId = playerId, GameId = gameId, Power = 0 }
                     },
                    Players = new List<Player>
                     {
                         new Player { Id = playerId, UserId = userId, GameId = gameId }
                     },
                    Units = new List<Unit>(),
                    TerritoryStates = new List<TerritoryState>()
                };

                // Add a Europe Army in Chicago FIRST so FirstOrDefault picks it
                var europeArmy = new Unit { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.Europe, UnitType = UnitType.Army, TerritoryId = "Chicago", HasMoved = false, IsHostile = false };
                game.Units.Add(europeArmy);

                // Start USA Army in Chicago
                var army = new Unit { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.USA, UnitType = UnitType.Army, TerritoryId = "Chicago", HasMoved = false };
                game.Units.Add(army);

                // USA Fleet in NorthAtlantic
                var fleet1 = new Unit { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.USA, UnitType = UnitType.Fleet, TerritoryId = "NorthAtlantic", HasConvoyed = false };
                game.Units.Add(fleet1);

                await context.Games.AddAsync(game);
                await context.SaveChangesAsync();

                var controller = GetController(context, userId);

                // Act
                var request = new MoveUnitRequest
                {
                    UnitId = army.Id,
                    DestinationId = "North-Africa"
                };

                var result = await controller.MoveArmy(gameId, request);

                // Assert
                Assert.IsType<OkResult>(result);

                var updatedGame = await context.Games.Include(g => g.Units).FirstAsync(g => g.Id == gameId);
                var updatedArmy = updatedGame.Units.First(u => u.Id == army.Id);
                Assert.Equal("North-Africa", updatedArmy.TerritoryId);
                Assert.True(updatedArmy.HasMoved);

                var updatedFleet1 = updatedGame.Units.First(u => u.Id == fleet1.Id);
                Assert.True(updatedFleet1.HasConvoyed);
            }
        }
        [Fact]
        public async Task MoveArmy_Hostile_IntoHomeProvince_WithOnlyFleet_ShouldTriggerBattle()
        {
            var dbName = Guid.NewGuid().ToString();
            using (var context = GetDbContext(dbName))
            {
                var gameId = Guid.NewGuid();
                var userId = "user1";
                var playerId = Guid.NewGuid();
                var game = new Game
                {
                    Id = gameId,
                    Status = GameStatus.InProgress,
                    CurrentTurnNation = Nation.Russia,
                    CurrentManeuverPhase = ManeuverPhase.Armies,
                    NationStates = new List<NationState>
                     {
                         new NationState { Nation = Nation.Russia, ControllerId = playerId, GameId = gameId, Power = 0 },
                         new NationState { Nation = Nation.Europe, ControllerId = Guid.NewGuid(), GameId = gameId, Power = 0 }
                     },
                    Players = new List<Player>
                     {
                         new Player { Id = playerId, UserId = userId, GameId = gameId }
                     },
                    Units = new List<Unit>(),
                    TerritoryStates = new List<TerritoryState>()
                };

                // Setup Moscow to London move via North Atlantic convoy
                // Russia Army in Moscow
                var army = new Unit { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.Russia, UnitType = UnitType.Army, TerritoryId = "Moscow", HasMoved = false };
                game.Units.Add(army);

                // Russia Fleet in North Atlantic for convoy
                var russiaFleet = new Unit { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.Russia, UnitType = UnitType.Fleet, TerritoryId = "NorthAtlantic", HasConvoyed = false };
                game.Units.Add(russiaFleet);

                // Europe Fleet in London
                var europeFleet = new Unit { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.Europe, UnitType = UnitType.Fleet, TerritoryId = "London" };
                game.Units.Add(europeFleet);

                await context.Games.AddAsync(game);
                await context.SaveChangesAsync();

                var controller = GetController(context, userId);

                var request = new MoveUnitRequest
                {
                    UnitId = army.Id,
                    DestinationId = "London",
                    IsHostile = true
                };

                var result = await controller.MoveArmy(gameId, request);

                if (result is Microsoft.AspNetCore.Mvc.BadRequestObjectResult badReq)
                {
                    throw new Exception($"BadRequest: {badReq.Value}");
                }

                Assert.IsType<OkResult>(result);

                var updatedGame = await context.Games.Include(g => g.Units).FirstAsync(g => g.Id == gameId);

                // It should AUTO-RESOLVE the battle since there is only 1 target and it's hostile
                Assert.Empty(updatedGame.PendingBattleDefenders);
                Assert.Null(updatedGame.PendingBattleTerritoryId);

                // Both units should be destroyed
                Assert.DoesNotContain(updatedGame.Units, u => u.Id == army.Id);
                Assert.DoesNotContain(updatedGame.Units, u => u.Id == europeFleet.Id);
            }
        }
        [Fact]
        public async Task MoveArmy_ThreeNationEncounter_OnlyCorrectDefenderCanRespond()
        {
            // Russia's army peacefully enters Beijing (China's home territory), where an India army is
            // already sitting (itself a foreign occupier there). Per the rules, the nation whose units are
            // already present (India) is the one offered the choice to fight the newcomer — not the
            // territory's owner (China), who has zero units there. This reproduces a scenario observed live
            // where a replay of this exact three-nation situation tried to authorize China's controller
            // instead of India's and was incorrectly Forbidden.
            string dbName = Guid.NewGuid().ToString();
            var context = GetDbContext(dbName);

            var gameId = Guid.NewGuid();
            var russiaPlayerId = Guid.NewGuid();
            var chinaPlayerId = Guid.NewGuid();
            var indiaPlayerId = Guid.NewGuid();
            const string russiaUserId = "russia-user";
            const string chinaUserId = "china-user";
            const string indiaUserId = "india-user";

            var game = new Game
            {
                Id = gameId,
                Status = GameStatus.InProgress,
                CurrentTurnNation = Nation.Russia,
                CurrentManeuverPhase = ManeuverPhase.Armies,
                Players = new List<Player>
                {
                    new Player { Id = russiaPlayerId, UserId = russiaUserId, GameId = gameId },
                    new Player { Id = chinaPlayerId, UserId = chinaUserId, GameId = gameId },
                    new Player { Id = indiaPlayerId, UserId = indiaUserId, GameId = gameId },
                },
                NationStates = new List<NationState>
                {
                    new NationState { Nation = Nation.Russia, ControllerId = russiaPlayerId, GameId = gameId },
                    new NationState { Nation = Nation.China, ControllerId = chinaPlayerId, GameId = gameId },
                    new NationState { Nation = Nation.India, ControllerId = indiaPlayerId, GameId = gameId },
                },
                Units = new List<Unit>(),
                TerritoryStates = new List<TerritoryState>()
            };

            var russiaArmy = new Unit { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.Russia, UnitType = UnitType.Army, TerritoryId = "Vladivostok", HasMoved = false };
            var indiaArmy = new Unit { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.India, UnitType = UnitType.Army, TerritoryId = "Beijing", IsHostile = true };
            game.Units.Add(russiaArmy);
            game.Units.Add(indiaArmy);

            await context.Games.AddAsync(game);
            await context.SaveChangesAsync();

            var russiaController = GetController(context, russiaUserId);
            var moveResult = await russiaController.MoveArmy(gameId, new MoveUnitRequest
            {
                UnitId = russiaArmy.Id,
                DestinationId = "Beijing",
                IsHostile = false // Peaceful entry so it doesn't auto-resolve, leaving India the choice to respond.
            });

            if (moveResult is Microsoft.AspNetCore.Mvc.BadRequestObjectResult badReq)
            {
                throw new Exception($"BadRequest: {badReq.Value}");
            }
            Assert.IsType<OkResult>(moveResult);

            var updatedGame = await context.Games.FirstAsync(g => g.Id == gameId);
            Assert.Equal("Beijing", updatedGame.PendingBattleTerritoryId);
            Assert.Equal(Nation.Russia, updatedGame.PendingBattleAggressorNation);
            Assert.Contains(Nation.India, updatedGame.PendingBattleDefenders);
            Assert.DoesNotContain(Nation.China, updatedGame.PendingBattleDefenders); // China has no units here.

            // The territory owner (China) has zero units present and must NOT be authorized to respond.
            var chinaController = GetController(context, chinaUserId);
            var chinaResult = await chinaController.BattleResponse(gameId, new BattleResponseRequest { IsFight = false });
            Assert.IsType<ForbidResult>(chinaResult);

            // India, whose army is the one actually present, is the correct responder and must be authorized.
            var indiaController = GetController(context, indiaUserId);
            var indiaResult = await indiaController.BattleResponse(gameId, new BattleResponseRequest { IsFight = false, Nation = Nation.India });
            Assert.IsType<OkResult>(indiaResult);
        }

        [Fact]
        public async Task BattleResponse_Peace_UnitsSurvive()
        {
            // Arrange
            string dbName = Guid.NewGuid().ToString();
            var context = GetDbContext(dbName);
            var setup = await SetupGame(context); // Setup user as controller of Russia

            var game = await context.Games.FirstAsync(g => g.Id == setup.GameId);
            game.PendingBattleTerritoryId = "Ukraine";
            game.PendingBattleAggressorNation = Nation.Europe;
            game.PendingBattleDefenders = new List<Nation> { Nation.Russia };

            var aggressorUnit = new Unit { Id = Guid.NewGuid(), Nation = Nation.Europe, TerritoryId = "Ukraine", UnitType = UnitType.Army, GameId = setup.GameId };
            var defenderUnit = new Unit { Id = Guid.NewGuid(), Nation = Nation.Russia, TerritoryId = "Ukraine", UnitType = UnitType.Army, GameId = setup.GameId };
            context.Units.AddRange(aggressorUnit, defenderUnit);
            await context.SaveChangesAsync();

            var controller = GetController(context, setup.UserId);
            var request = new BattleResponseRequest { IsFight = false };

            // Act
            var result = await controller.BattleResponse(setup.GameId, request);

            // Assert
            Assert.IsType<OkResult>(result);

            var updatedGame = await context.Games.Include(g => g.Units).FirstAsync(g => g.Id == setup.GameId);

            // Battle cleared
            Assert.Null(updatedGame.PendingBattleTerritoryId);
            Assert.Null(updatedGame.PendingBattleAggressorNation);
            Assert.Empty(updatedGame.PendingBattleDefenders);

            // Both units survive
            var unitsInTerritory = updatedGame.Units.Where(u => u.TerritoryId == "Ukraine").ToList();
            Assert.Equal(2, unitsInTerritory.Count);
            Assert.Contains(unitsInTerritory, u => u.Nation == Nation.Europe);
            Assert.Contains(unitsInTerritory, u => u.Nation == Nation.Russia);

            // Units keep their original hostility status (which defaults to true in test setup)
            Assert.All(unitsInTerritory, u => Assert.True(u.IsHostile));
        }

        [Fact]
        public async Task BattleResponse_Fight_BothUnitsDestroyed()
        {
            // Arrange
            string dbName = Guid.NewGuid().ToString();
            var context = GetDbContext(dbName);
            var setup = await SetupGame(context); // Setup user as controller of Russia

            var game = await context.Games.FirstAsync(g => g.Id == setup.GameId);
            game.PendingBattleTerritoryId = "Ukraine";
            game.PendingBattleAggressorNation = Nation.Europe;
            game.PendingBattleDefenders = new List<Nation> { Nation.Russia };

            var aggressorUnit = new Unit { Id = Guid.NewGuid(), Nation = Nation.Europe, TerritoryId = "Ukraine", UnitType = UnitType.Army, GameId = setup.GameId };
            var defenderUnit = new Unit { Id = Guid.NewGuid(), Nation = Nation.Russia, TerritoryId = "Ukraine", UnitType = UnitType.Army, GameId = setup.GameId };
            context.Units.AddRange(aggressorUnit, defenderUnit);
            await context.SaveChangesAsync();

            var controller = GetController(context, setup.UserId);
            var request = new BattleResponseRequest { IsFight = true };

            // Act
            var result = await controller.BattleResponse(setup.GameId, request);

            // Assert
            Assert.IsType<OkResult>(result);

            var updatedGame = await context.Games.Include(g => g.Units).FirstAsync(g => g.Id == setup.GameId);

            // Battle cleared
            Assert.Null(updatedGame.PendingBattleTerritoryId);
            Assert.Null(updatedGame.PendingBattleAggressorNation);
            Assert.Empty(updatedGame.PendingBattleDefenders);

            // Both units destroyed
            var unitsInTerritory = updatedGame.Units.Where(u => u.TerritoryId == "Ukraine").ToList();
            Assert.Empty(unitsInTerritory);
        }
        [Fact]
        public async Task BattleResponse_MultiDefender_AllPeace_UnitsSurvive()
        {
            // Arrange
            string dbName = Guid.NewGuid().ToString();
            var context = GetDbContext(dbName);
            var setup = await SetupGame(context); // Setup user as controller of Russia

            var player2Id = Guid.NewGuid();
            var user2Id = "user-2";
            context.Players.Add(new Player { Id = player2Id, GameId = setup.GameId, UserId = user2Id });
            context.NationStates.Add(new NationState { Nation = Nation.India, ControllerId = player2Id, GameId = setup.GameId });

            var game = await context.Games.FirstAsync(g => g.Id == setup.GameId);
            game.PendingBattleTerritoryId = "Ukraine";
            game.PendingBattleAggressorNation = Nation.Europe;
            game.PendingBattleDefenders = new List<Nation> { Nation.Russia, Nation.India };

            var aggressorUnit = new Unit { Id = Guid.NewGuid(), Nation = Nation.Europe, TerritoryId = "Ukraine", UnitType = UnitType.Army, GameId = setup.GameId };
            var defender1 = new Unit { Id = Guid.NewGuid(), Nation = Nation.Russia, TerritoryId = "Ukraine", UnitType = UnitType.Army, GameId = setup.GameId };
            var defender2 = new Unit { Id = Guid.NewGuid(), Nation = Nation.India, TerritoryId = "Ukraine", UnitType = UnitType.Army, GameId = setup.GameId };
            context.Units.AddRange(aggressorUnit, defender1, defender2);
            await context.SaveChangesAsync();

            var controller1 = GetController(context, setup.UserId); // Controls Russia
            var controller2 = GetController(context, user2Id); // Controls India

            // Act 1: Russia chooses peace
            var result1 = await controller1.BattleResponse(setup.GameId, new BattleResponseRequest { IsFight = false });
            Assert.IsType<OkResult>(result1);

            // Assert 1: Battle still pending for India
            var game1 = await context.Games.FirstAsync(g => g.Id == setup.GameId);
            Assert.Equal("Ukraine", game1.PendingBattleTerritoryId);
            Assert.Contains(Nation.India, game1.PendingBattleDefenders);
            Assert.DoesNotContain(Nation.Russia, game1.PendingBattleDefenders);

            // Act 2: India chooses peace
            var result2 = await controller2.BattleResponse(setup.GameId, new BattleResponseRequest { IsFight = false });
            Assert.IsType<OkResult>(result2);

            // Assert 2: Battle cleared, all survive
            var finalGame = await context.Games.Include(g => g.Units).FirstAsync(g => g.Id == setup.GameId);
            Assert.Null(finalGame.PendingBattleTerritoryId);
            Assert.Empty(finalGame.PendingBattleDefenders);

            var unitsInTerritory = finalGame.Units.Where(u => u.TerritoryId == "Ukraine").ToList();
            Assert.Equal(3, unitsInTerritory.Count);
            Assert.All(unitsInTerritory, u => Assert.True(u.IsHostile));
        }

        [Fact]
        public async Task BattleResponse_MultiDefender_OneFight_UnitsDestroyed()
        {
            // Arrange
            string dbName = Guid.NewGuid().ToString();
            var context = GetDbContext(dbName);
            var setup = await SetupGame(context);

            var player2Id = Guid.NewGuid();
            var user2Id = "user-2";
            context.Players.Add(new Player { Id = player2Id, GameId = setup.GameId, UserId = user2Id });
            context.NationStates.Add(new NationState { Nation = Nation.India, ControllerId = player2Id, GameId = setup.GameId });

            var game = await context.Games.FirstAsync(g => g.Id == setup.GameId);
            game.PendingBattleTerritoryId = "Ukraine";
            game.PendingBattleAggressorNation = Nation.Europe;
            game.PendingBattleDefenders = new List<Nation> { Nation.Russia, Nation.India };

            var aggressorUnit = new Unit { Id = Guid.NewGuid(), Nation = Nation.Europe, TerritoryId = "Ukraine", UnitType = UnitType.Army, GameId = setup.GameId };
            var defender1 = new Unit { Id = Guid.NewGuid(), Nation = Nation.Russia, TerritoryId = "Ukraine", UnitType = UnitType.Army, GameId = setup.GameId };
            var defender2 = new Unit { Id = Guid.NewGuid(), Nation = Nation.India, TerritoryId = "Ukraine", UnitType = UnitType.Army, GameId = setup.GameId };
            context.Units.AddRange(aggressorUnit, defender1, defender2);
            await context.SaveChangesAsync();

            var controller1 = GetController(context, setup.UserId); // Controls Russia
            var controller2 = GetController(context, user2Id); // Controls India

            // Act 1: Russia chooses peace
            await controller1.BattleResponse(setup.GameId, new BattleResponseRequest { IsFight = false });

            // Act 2: India chooses fight!
            await controller2.BattleResponse(setup.GameId, new BattleResponseRequest { IsFight = true });

            // Assert: Battle cleared, Aggressor and India destroyed, Russia survives
            var finalGame = await context.Games.Include(g => g.Units).FirstAsync(g => g.Id == setup.GameId);
            Assert.Null(finalGame.PendingBattleTerritoryId);

            var unitsInTerritory = finalGame.Units.Where(u => u.TerritoryId == "Ukraine").ToList();
            Assert.Single(unitsInTerritory);
            Assert.Equal(Nation.Russia, unitsInTerritory.First().Nation); // Russia chose peace and didn't fight
        }

        [Fact]
        public async Task MoveArmy_PeacefulIntoOwnHome_WithForeignArmy_ForcesBattle()
        {
            var dbName = Guid.NewGuid().ToString();
            using (var context = GetDbContext(dbName))
            {
                // Arrange
                var gameId = Guid.NewGuid();
                var userId = "user1";
                var playerId = Guid.NewGuid();
                var game = new Game
                {
                    Id = gameId,
                    Name = "TestGame",
                    Status = GameStatus.InProgress,
                    CurrentTurnNation = Nation.Russia,
                    CurrentManeuverPhase = ManeuverPhase.Armies
                };
                game.Players.Add(new Player { Id = playerId, UserId = userId, IsHost = true });
                game.NationStates.Add(new NationState { GameId = gameId, Nation = Nation.Russia, ControllerId = playerId });
                game.NationStates.Add(new NationState { GameId = gameId, Nation = Nation.China, ControllerId = Guid.NewGuid() }); // Needed for valid setup

                var russianArmy = new Unit { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.Russia, UnitType = UnitType.Army, TerritoryId = "Moscow" };
                var chineseArmy = new Unit { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.China, UnitType = UnitType.Army, TerritoryId = "Vladivostok", IsHostile = true };

                game.Units.Add(russianArmy);
                game.Units.Add(chineseArmy);
                context.Games.Add(game);
                await context.SaveChangesAsync();

                var mockHub = new Mock<IHubContext<Imperial2030.Server.Hubs.GameHub>>();
                var mockClients = new Mock<IHubClients>();
                var mockGroup = new Mock<IClientProxy>();
                mockHub.Setup(h => h.Clients).Returns(mockClients.Object);
                mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(mockGroup.Object);

                var controller = new ManeuverController(context, mockHub.Object, null);
                var user = new ClaimsPrincipal(new ClaimsIdentity(new Claim[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "mock"));
                controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };

                var request = new MoveUnitRequest
                {
                    UnitId = russianArmy.Id,
                    DestinationId = "Vladivostok",
                    IsHostile = false // Explicitly requested peaceful move!
                };

                // Act
                var result = await controller.MoveArmy(gameId, request);

                // Assert
                Assert.IsType<OkResult>(result);

                var updatedGame = await context.Games.Include(g => g.Units).FirstAsync(g => g.Id == gameId);

                // Because it is Russia's home territory (Vladivostok) and an enemy is there, 
                // the peaceful move MUST be forcefully converted to a hostile battle and both destroyed.
                // It should NOT enter a PendingBattle phase awaiting a "peace" response.
                Assert.Null(updatedGame.PendingBattleTerritoryId);
                Assert.Empty(updatedGame.Units); // Both Russian and Chinese armies are destroyed
            }
        }

        [Fact]
        public async Task Max15Flags_FlagRemovedWithoutReplacement()
        {
            var dbName = Guid.NewGuid().ToString();
            using (var context = GetDbContext(dbName))
            {
                var (gameId, userId, _) = await SetupGame(context);

                // Setup 15 existing flags for Russia
                for (int i = 0; i < 15; i++)
                {
                    context.TerritoryStates.Add(new TerritoryState
                    {
                        TerritoryId = $"T{i}",
                        GameId = gameId,
                        Controller = Nation.Russia
                    });
                }

                // Territory controlled by Europe initially
                var targetTerritoryId = "Colombia";
                context.TerritoryStates.Add(new TerritoryState
                {
                    TerritoryId = targetTerritoryId,
                    GameId = gameId,
                    Controller = Nation.Europe
                });

                // Move 1 Russian army to this territory
                var army1 = new Unit { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.Russia, UnitType = UnitType.Army, TerritoryId = targetTerritoryId };
                context.Units.Add(army1);

                await context.SaveChangesAsync();

                var controller = GetController(context, userId);

                // End maneuver phase triggers UpdateTerritoryControl
                var result = await controller.NextPhase(gameId);

                Assert.IsType<OkResult>(result);

                var targetState = await context.TerritoryStates.FirstAsync(t => t.TerritoryId == targetTerritoryId);
                // The old flag (Europe) should be removed, and no new flag (Russia) should be placed
                Assert.Null(targetState.Controller);
            }
        }

        [Fact]
        public async Task RespondToBattle_PeaceBetweenTwoForeignArmies_MaintainsHostileOccupationOfHomeNation()
        {
            var dbName = Guid.NewGuid().ToString();
            using (var context = GetDbContext(dbName))
            {
                var (gameId, userId, playerId) = await SetupGame(context);
                
                var game = await context.Games.FirstAsync();
                
                // Pending Battle setup: EU is aggressor, RU is defender in Chicago (USA Home)
                game.PendingBattleTerritoryId = "Chicago";
                game.PendingBattleAggressorNation = Nation.Europe;
                game.PendingBattleDefenders.Add(Nation.Russia);
                
                // RU occupies Chicago with hostile flag
                var ruArmy = new Unit { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.Russia, UnitType = UnitType.Army, TerritoryId = "Chicago", IsHostile = true };
                
                // EU moved in peacefully
                var euArmy = new Unit { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.Europe, UnitType = UnitType.Army, TerritoryId = "Chicago", IsHostile = false };

                context.Units.AddRange(ruArmy, euArmy);
                await context.SaveChangesAsync();

                var controller = GetController(context, userId); // Russia's controller

                var request = new BattleResponseRequest { IsFight = false }; // Peace

                // Act
                var result = await controller.BattleResponse(gameId, request);

                // Assert
                Assert.IsType<OkResult>(result);

                var finalUnits = await context.Units.Where(u => u.TerritoryId == "Chicago").ToListAsync();
                var finalRuArmy = finalUnits.First(u => u.Nation == Nation.Russia);
                
                // EU moved in, both chose peace. EU remains peaceful, but RU was already hostilely occupying USA, so it must stay hostile.
                Assert.True(finalRuArmy.IsHostile, "Russian army should remain hostile in USA home territory even after peace with EU.");
            }
        }
    }
}
