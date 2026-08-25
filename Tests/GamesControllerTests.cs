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
using Microsoft.Extensions.Logging;
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
            mockServiceProvider.Setup(sp => sp.GetService(typeof(Imperial2030.Server.Services.INotificationService))).Returns(new Moq.Mock<Imperial2030.Server.Services.INotificationService>().Object);
            mockScopeFactory.Setup(s => s.CreateScope()).Returns(() =>
            {
                var scope = new Mock<IServiceScope>();
                scope.Setup(s => s.ServiceProvider).Returns(mockServiceProvider.Object);
                return scope.Object;
            });
            var mockBotServiceLogger = new Mock<ILogger<BotService>>();
            var botService = new BotService(mockScopeFactory.Object, mockHub.Object, new System.Collections.Generic.List<Imperial2030.Server.Services.Bots.IBotStrategy> { new Imperial2030.Server.Services.Bots.Strategies.DefaultBotStrategy() }, mockBotServiceLogger.Object);

            var controller = new GamesController(context, mockUserManager.Object, mockHub.Object, presenceTracker, botService, new Moq.Mock<Imperial2030.Server.Services.INotificationService>().Object);

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

        // Production is one action taken on landing (Imperial-2030-Rules.pdf p.7 "Production"), not a
        // repeatable one. HasProducedThisTurn was previously only ever WRITTEN — never read as a guard,
        // unlike its siblings HasBuiltThisTurn (BuildFactory) and HasImportedThisTurn (ExecuteImport) —
        // so re-POSTing the endpoint produced another full batch of free units, up to the unit cap.
        [Fact]
        public async Task ExecuteProduction_CalledTwiceInSameTurn_SecondCallIsRejected()
        {
            var dbName = Guid.NewGuid().ToString();
            using (var context = GetDbContext(dbName))
            {
                // RondelPosition 2 is Production
                var (gameId, userId, _) = await SetupGame(context, rondelPosition: 2);

                var moscowId = "Moscow";
                var vladivostokId = "Vladivostok";

                context.TerritoryStates.AddRange(
                    new TerritoryState { TerritoryId = moscowId, GameId = gameId, Controller = Nation.Russia, HasFactory = true },
                    new TerritoryState { TerritoryId = vladivostokId, GameId = gameId, Controller = Nation.Russia, HasFactory = true });
                await context.SaveChangesAsync();

                var controller = GetController(context, userId);

                // First call: both factories are unblocked, so both produce.
                var first = await controller.ExecuteProduction(gameId);
                Assert.IsType<OkObjectResult>(first);

                var unitsAfterFirst = await context.Units.Where(u => u.GameId == gameId).ToListAsync();
                Assert.Equal(2, unitsAfterFirst.Count);

                var nsAfterFirst = await context.NationStates.FirstAsync(n => n.GameId == gameId && n.Nation == Nation.Russia);
                Assert.True(nsAfterFirst.HasProducedThisTurn);

                // Second call in the same turn must be refused outright, not silently produce again.
                var second = await controller.ExecuteProduction(gameId);
                var badRequest = Assert.IsType<BadRequestObjectResult>(second);
                Assert.Equal("Already produced this turn.", badRequest.Value);

                var unitsAfterSecond = await context.Units.Where(u => u.GameId == gameId).ToListAsync();
                Assert.Equal(2, unitsAfterSecond.Count);
            }
        }

        // The nation's turn is suspended while an Investor phase resolves, so no slot action may run —
        // BuildFactory already guarded this and ExecuteProduction did not.
        [Fact]
        public async Task ExecuteProduction_DuringInvestorTurn_IsRejected()
        {
            var dbName = Guid.NewGuid().ToString();
            using (var context = GetDbContext(dbName))
            {
                var (gameId, userId, _) = await SetupGame(context, rondelPosition: 2);

                context.TerritoryStates.Add(
                    new TerritoryState { TerritoryId = "Moscow", GameId = gameId, Controller = Nation.Russia, HasFactory = true });

                var game = await context.Games.FirstAsync(g => g.Id == gameId);
                game.IsInvestorTurn = true;
                await context.SaveChangesAsync();

                var controller = GetController(context, userId);

                var result = await controller.ExecuteProduction(gameId);

                var badRequest = Assert.IsType<BadRequestObjectResult>(result);
                Assert.Equal("Waiting for Investor Phase.", badRequest.Value);
                Assert.Empty(await context.Units.Where(u => u.GameId == gameId).ToListAsync());
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

        [Theory]
        [InlineData(true, 0)]
        [InlineData(false, 1)]
        public async Task ExecuteTaxation_VariantBonusOnlyForTaxIncreases_AppliesCorrectly(bool isVariantActive, int expectedBonus)
        {
            var dbName = Guid.NewGuid().ToString();
            using (var context = GetDbContext(dbName))
            {
                var (gameId, userId, _) = await SetupGame(context, rondelPosition: 0);

                var game = await context.Games.FindAsync(gameId);
                game.VariantBonusOnlyForTaxIncreases = isVariantActive;

                // Setup Russia: TaxRevenue was 6 (Tier 1). Normally a tax of 6 yields 1M bonus.
                var nationState = await context.NationStates.FirstAsync(n => n.GameId == gameId && n.Nation == Nation.Russia);
                nationState.TaxRevenue = 6;
                nationState.Treasury = 10;

                var controllerPlayer = await context.Players.FirstAsync(p => p.UserId == userId);
                controllerPlayer.Cash = 0;

                // Set up territories so tax revenue equals 6 (Tier 1) again
                // 1 Factory + 4 Flags = 2 + 4 = 6.
                var t1 = new TerritoryState { TerritoryId = "Moscow", GameId = gameId, Controller = Nation.Russia, HasFactory = true };
                var t2 = new TerritoryState { TerritoryId = "Vladivostok", GameId = gameId, Controller = Nation.Russia, HasFactory = false };
                var t3 = new TerritoryState { TerritoryId = "StPetersburg", GameId = gameId, Controller = Nation.Russia, HasFactory = false };
                var t4 = new TerritoryState { TerritoryId = "Kiev", GameId = gameId, Controller = Nation.Russia, HasFactory = false };

                context.TerritoryStates.AddRange(t1, t2, t3, t4);
                await context.SaveChangesAsync();

                var controller = GetController(context, userId);

                // Act
                var result = await controller.ExecuteTaxation(gameId);

                // Assert
                Assert.IsType<OkObjectResult>(result);

                var updatedController = await context.Players.FirstAsync(p => p.UserId == userId);

                Assert.Equal(expectedBonus, updatedController.Cash);
            }
        }

        [Fact]
        public async Task GetGames_TellsTheCallerAboutThemselves_WithoutExposingAnyoneElsesUserId()
        {
            // The lobby list is [AllowAnonymous]. It used to hand every caller `UserIds` - the raw ASP.NET
            // Identity GUID of every player in every game - plus the host's, when the client only ever
            // asked two questions about the CALLER: am I in this game, and do I host it. Those are now
            // answered server-side as booleans, so no user id is served to anyone.
            var dbName = Guid.NewGuid().ToString();
            using var context = GetDbContext(dbName);

            var gameId = Guid.NewGuid();
            context.Games.Add(new Game { Id = gameId, Name = "Lobby Game", Status = GameStatus.Lobby, IsPrivate = false });
            context.Players.AddRange(
                new Player { Id = Guid.NewGuid(), GameId = gameId, UserId = "host-user", IsHost = true },
                new Player { Id = Guid.NewGuid(), GameId = gameId, UserId = "other-user", IsHost = false });
            await context.SaveChangesAsync();

            // The host sees both flags set.
            var asHost = Assert.IsType<List<GameDto>>(((await GetController(context, "host-user").GetGames()).Value)?.ToList());
            var hostView = Assert.Single(asHost);
            Assert.True(hostView.IsCurrentUserInGame);
            Assert.True(hostView.IsCurrentUserHost);

            // A player who is in the game but does not host it.
            var asOther = Assert.IsType<List<GameDto>>(((await GetController(context, "other-user").GetGames()).Value)?.ToList());
            var otherView = Assert.Single(asOther);
            Assert.True(otherView.IsCurrentUserInGame);
            Assert.False(otherView.IsCurrentUserHost);

            // A stranger - and, by the same path, an anonymous caller - sees neither.
            var asStranger = Assert.IsType<List<GameDto>>(((await GetController(context, "nobody").GetGames()).Value)?.ToList());
            var strangerView = Assert.Single(asStranger);
            Assert.False(strangerView.IsCurrentUserInGame);
            Assert.False(strangerView.IsCurrentUserHost);
        }

        [Fact]
        public async Task SwissBank_PlayerWithoutNations_CanInvestAndGainControl()
        {
            var dbName = Guid.NewGuid().ToString();
            using (var context = GetDbContext(dbName))
            {
                var gameId = Guid.NewGuid();
                var p1UserId = "p1-user";
                var p2UserId = "p2-user";
                var p1Id = Guid.NewGuid();
                var p2Id = Guid.NewGuid();

                var game = new Game
                {
                    Id = gameId,
                    CurrentTurnNation = Nation.Russia,
                    Status = GameStatus.InProgress,
                    InvestorCardHolderId = p1Id // P1 holds the investor card
                };

                // P1 controls Russia
                var p1 = new Player { Id = p1Id, GameId = gameId, UserId = p1UserId, Cash = 10 };
                // P2 has NO nations, and is therefore a Swiss Bank player
                var p2 = new Player { Id = p2Id, GameId = gameId, UserId = p2UserId, Cash = 20 };

                var nsRussia = new NationState
                {
                    Nation = Nation.Russia,
                    ControllerId = p1Id,
                    GameId = gameId,
                    RondelPosition = 3 // Suppose moving to Investor (4)
                };

                var bond9M = new Bond { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.Russia, Cost = 9, Interest = 3, HolderId = null }; // Available in bank
                var bond2M = new Bond { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.Russia, Cost = 2, Interest = 1, HolderId = p1Id }; // P1 owns 2M

                context.Games.Add(game);
                context.Players.AddRange(p1, p2);
                context.NationStates.Add(nsRussia);
                context.Bonds.AddRange(bond9M, bond2M);
                await context.SaveChangesAsync();

                var controllerP1 = GetController(context, p1UserId);

                // Act 1: P1 (Russia) moves to Investor slot
                await controllerP1.MoveNation(gameId, Nation.Russia, 4);

                // Assert 1: the Investor CARD HOLDER (P1) invests first.
                // Imperial-2030-Rules.pdf p.11 numbers the steps "2. Activating the Investor" then
                // "3. Investing as Swiss Bank". This assertion used to expect P2 ("Swiss Bank players go
                // first"), which is the opposite of the rulebook and had no source behind it.
                var updatedGame = await context.Games.FirstAsync(g => g.Id == gameId);
                Assert.True(updatedGame.IsInvestorTurn);
                Assert.Equal(p1Id, updatedGame.ActingPlayerId);

                // Act 2: P1 takes their turn and passes, handing the queue to the Swiss Bank player.
                await controllerP1.PerformInvestment(gameId, new GamesController.InvestmentActionDto { ActionType = "Pass" });

                // Assert 2: P2 (Swiss Bank) is now up.
                updatedGame = await context.Games.FirstAsync(g => g.Id == gameId);
                Assert.Equal(p2Id, updatedGame.ActingPlayerId);

                // Act 3: P2 invests 9M into Russia
                var controllerP2 = GetController(context, p2UserId);
                var investRequest = new GamesController.InvestmentActionDto { ActionType = "Buy", BondId = bond9M.Id };
                await controllerP2.PerformInvestment(gameId, investRequest);

                // Assert 3: P2 outbid P1's 2M and takes over Russia - the point of this test.
                var updatedNsRussia = await context.NationStates.FirstAsync(n => n.Nation == Nation.Russia);
                Assert.Equal(p2Id, updatedNsRussia.ControllerId);
            }
        }

        [Fact]
        public async Task PerformInvestment_TradeInBond_UpgradesCorrectly()
        {
            var dbName = Guid.NewGuid().ToString();
            using (var context = GetDbContext(dbName))
            {
                var gameId = Guid.NewGuid();
                var p1UserId = "p1-user";
                var p1Id = Guid.NewGuid();

                var game = new Game
                {
                    Id = gameId,
                    CurrentTurnNation = Nation.Russia,
                    Status = GameStatus.InProgress,
                    InvestorCardHolderId = p1Id,
                    IsInvestorTurn = true,
                    ActingPlayerId = p1Id
                };

                // P1 has 10M cash and owns 4M Russia bond. They want to buy 9M Russia bond.
                var p1 = new Player { Id = p1Id, GameId = gameId, UserId = p1UserId, Cash = 10 };

                var nsRussia = new NationState
                {
                    Nation = Nation.Russia,
                    ControllerId = p1Id,
                    GameId = gameId,
                    Treasury = 0
                };

                var bond9M = new Bond { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.Russia, Cost = 9, Interest = 3, HolderId = null };
                var bond4M = new Bond { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.Russia, Cost = 4, Interest = 2, HolderId = p1Id };

                context.Games.Add(game);
                context.Players.Add(p1);
                context.NationStates.Add(nsRussia);
                context.Bonds.AddRange(bond9M, bond4M);
                await context.SaveChangesAsync();

                var controller = GetController(context, p1UserId);

                // Act: P1 trades in 4M bond for 9M bond (costing 5M)
                var investRequest = new GamesController.InvestmentActionDto { ActionType = "Buy", BondId = bond9M.Id, TradeInBondId = bond4M.Id };
                var result = await controller.PerformInvestment(gameId, investRequest);

                // Assert
                Assert.IsType<OkResult>(result);

                var updatedP1 = await context.Players.FirstAsync(p => p.Id == p1Id);
                Assert.Equal(5, updatedP1.Cash); // 10M - (9M - 4M) = 5M

                var updatedNsRussia = await context.NationStates.FirstAsync(n => n.Nation == Nation.Russia);
                Assert.Equal(5, updatedNsRussia.Treasury); // Russia gains 5M

                var updatedBond9M = await context.Bonds.FirstAsync(b => b.Id == bond9M.Id);
                Assert.Equal(p1Id, updatedBond9M.HolderId); // P1 owns 9M bond

                var updatedBond4M = await context.Bonds.FirstAsync(b => b.Id == bond4M.Id);
                Assert.Null(updatedBond4M.HolderId); // 4M bond returned to bank
            }
        }

        [Fact]
        public async Task BuildFactory_GameNotInProgress_ReturnsBadRequest()
        {
            var dbName = Guid.NewGuid().ToString();
            using (var context = GetDbContext(dbName))
            {
                var (gameId, userId, _) = await SetupGame(context, rondelPosition: 1);
                var game = await context.Games.FindAsync(gameId);
                game.Status = GameStatus.Lobby;
                await context.SaveChangesAsync();

                var controller = GetController(context, userId);
                var result = await controller.BuildFactory(gameId, "Moscow");

                var badRequest = Assert.IsType<BadRequestObjectResult>(result);
                Assert.Contains("Game not in progress", badRequest.Value.ToString());
            }
        }

        [Fact]
        public async Task BuildFactory_IsInvestorTurn_ReturnsBadRequest()
        {
            var dbName = Guid.NewGuid().ToString();
            using (var context = GetDbContext(dbName))
            {
                var (gameId, userId, _) = await SetupGame(context, rondelPosition: 1);
                var game = await context.Games.FindAsync(gameId);
                game.IsInvestorTurn = true;
                await context.SaveChangesAsync();

                var controller = GetController(context, userId);
                var result = await controller.BuildFactory(gameId, "Moscow");

                var badRequest = Assert.IsType<BadRequestObjectResult>(result);
                Assert.Contains("Waiting for Investor Phase", badRequest.Value.ToString());
            }
        }

        [Fact]
        public async Task BuildFactory_NoController_ReturnsBadRequest()
        {
            var dbName = Guid.NewGuid().ToString();
            using (var context = GetDbContext(dbName))
            {
                var (gameId, userId, _) = await SetupGame(context, rondelPosition: 1);
                var ns = await context.NationStates.FirstAsync(n => n.Nation == Nation.Russia);
                ns.ControllerId = null;
                await context.SaveChangesAsync();

                var controller = GetController(context, userId);
                var result = await controller.BuildFactory(gameId, "Moscow");

                var badRequest = Assert.IsType<BadRequestObjectResult>(result);
                Assert.Contains("No controller for this nation", badRequest.Value.ToString());
            }
        }

        [Fact]
        public async Task BuildFactory_NotController_ReturnsForbid()
        {
            var dbName = Guid.NewGuid().ToString();
            using (var context = GetDbContext(dbName))
            {
                var (gameId, _, _) = await SetupGame(context, rondelPosition: 1);
                var wrongUserId = "wrong-user";
                
                var controller = GetController(context, wrongUserId);
                var result = await controller.BuildFactory(gameId, "Moscow");

                Assert.IsType<ForbidResult>(result);
            }
        }

        [Fact]
        public async Task BuildFactory_WrongRondelPosition_ReturnsBadRequest()
        {
            var dbName = Guid.NewGuid().ToString();
            using (var context = GetDbContext(dbName))
            {
                // 2 is Production, not Factory
                var (gameId, userId, _) = await SetupGame(context, rondelPosition: 2);

                var controller = GetController(context, userId);
                var result = await controller.BuildFactory(gameId, "Moscow");

                var badRequest = Assert.IsType<BadRequestObjectResult>(result);
                Assert.Contains("Nation must be on 'Factory' slot", badRequest.Value.ToString());
            }
        }

        [Fact]
        public async Task BuildFactory_AlreadyBuiltThisTurn_ReturnsBadRequest()
        {
            var dbName = Guid.NewGuid().ToString();
            using (var context = GetDbContext(dbName))
            {
                var (gameId, userId, _) = await SetupGame(context, rondelPosition: 1);
                var ns = await context.NationStates.FirstAsync(n => n.Nation == Nation.Russia);
                ns.HasBuiltThisTurn = true;
                await context.SaveChangesAsync();

                var controller = GetController(context, userId);
                var result = await controller.BuildFactory(gameId, "Moscow");

                var badRequest = Assert.IsType<BadRequestObjectResult>(result);
                Assert.Contains("Already built factory this turn", badRequest.Value.ToString());
            }
        }

        [Fact]
        public async Task BuildFactory_InvalidTerritory_ReturnsBadRequest()
        {
            var dbName = Guid.NewGuid().ToString();
            using (var context = GetDbContext(dbName))
            {
                var (gameId, userId, _) = await SetupGame(context, rondelPosition: 1);

                var controller = GetController(context, userId);
                var result = await controller.BuildFactory(gameId, "InvalidName");

                var badRequest = Assert.IsType<BadRequestObjectResult>(result);
                Assert.Contains("Invalid territory", badRequest.Value.ToString());
            }
        }

        [Fact]
        public async Task BuildFactory_NotHomeCity_ReturnsBadRequest()
        {
            var dbName = Guid.NewGuid().ToString();
            using (var context = GetDbContext(dbName))
            {
                var (gameId, userId, _) = await SetupGame(context, rondelPosition: 1);

                var controller = GetController(context, userId);
                // Paris is Europe's home city, not Russia's
                var result = await controller.BuildFactory(gameId, "Paris");

                var badRequest = Assert.IsType<BadRequestObjectResult>(result);
                Assert.Contains("Can only build in Russia's home cities", badRequest.Value.ToString());
            }
        }

        [Fact]
        public async Task BuildFactory_FactoryAlreadyExists_ReturnsBadRequest()
        {
            var dbName = Guid.NewGuid().ToString();
            using (var context = GetDbContext(dbName))
            {
                var (gameId, userId, _) = await SetupGame(context, rondelPosition: 1);
                
                var tMoscow = new TerritoryState { TerritoryId = "Moscow", GameId = gameId, HasFactory = true };
                context.TerritoryStates.Add(tMoscow);
                await context.SaveChangesAsync();

                var controller = GetController(context, userId);
                var result = await controller.BuildFactory(gameId, "Moscow");

                var badRequest = Assert.IsType<BadRequestObjectResult>(result);
                Assert.Contains("Factory already exists", badRequest.Value.ToString());
            }
        }

        [Fact]
        public async Task BuildFactory_HostileArmyPresent_ReturnsBadRequest()
        {
            var dbName = Guid.NewGuid().ToString();
            using (var context = GetDbContext(dbName))
            {
                // RondelPosition 1 is Factory
                var (gameId, userId, _) = await SetupGame(context, rondelPosition: 1);

                // Give nation enough treasury
                var ns = await context.NationStates.FirstAsync(n => n.Nation == Nation.Russia);
                ns.Treasury = 10;
                await context.SaveChangesAsync();

                var moscowId = "Moscow";
                var tMoscow = new TerritoryState { TerritoryId = moscowId, GameId = gameId, Controller = Nation.Russia, HasFactory = false };

                // Hostile army in Moscow
                var hostileArmy = new Unit { Id = Guid.NewGuid(), GameId = gameId, Nation = Nation.China, UnitType = UnitType.Army, TerritoryId = moscowId, IsHostile = true };

                context.TerritoryStates.Add(tMoscow);
                context.Units.Add(hostileArmy);
                await context.SaveChangesAsync();

                var controller = GetController(context, userId);
                var result = await controller.BuildFactory(gameId, moscowId);

                var badRequest = Assert.IsType<BadRequestObjectResult>(result);
                Assert.Contains("hostile foreign armies", badRequest.Value.ToString());
            }
        }

        [Fact]
        public async Task BuildFactory_InsufficientTreasury_ReturnsBadRequest()
        {
            var dbName = Guid.NewGuid().ToString();
            using (var context = GetDbContext(dbName))
            {
                var (gameId, userId, _) = await SetupGame(context, rondelPosition: 1);
                var ns = await context.NationStates.FirstAsync(n => n.Nation == Nation.Russia);
                ns.Treasury = 4; // Less than 5
                
                var tMoscow = new TerritoryState { TerritoryId = "Moscow", GameId = gameId, HasFactory = false };
                context.TerritoryStates.Add(tMoscow);
                await context.SaveChangesAsync();

                var controller = GetController(context, userId);
                var result = await controller.BuildFactory(gameId, "Moscow");

                var badRequest = Assert.IsType<BadRequestObjectResult>(result);
                Assert.Contains("Nation treasury insufficient", badRequest.Value.ToString());
            }
        }

        [Fact]
        public async Task BuildFactory_HappyPath_BuildsFactoryAndDeductsTreasury()
        {
            var dbName = Guid.NewGuid().ToString();
            using (var context = GetDbContext(dbName))
            {
                var (gameId, userId, _) = await SetupGame(context, rondelPosition: 1);
                var ns = await context.NationStates.FirstAsync(n => n.Nation == Nation.Russia);
                ns.Treasury = 10; // Enough for 5M cost
                
                var tMoscow = new TerritoryState { TerritoryId = "Moscow", GameId = gameId, HasFactory = false };
                context.TerritoryStates.Add(tMoscow);
                await context.SaveChangesAsync();

                var controller = GetController(context, userId);
                var result = await controller.BuildFactory(gameId, "Moscow");

                Assert.IsType<OkResult>(result);

                var updatedNs = await context.NationStates.FirstAsync(n => n.Nation == Nation.Russia);
                Assert.Equal(5, updatedNs.Treasury);
                Assert.True(updatedNs.HasBuiltThisTurn);

                var updatedTerritory = await context.TerritoryStates.FirstAsync(t => t.TerritoryId == "Moscow");
                Assert.True(updatedTerritory.HasFactory);
            }
        }
        [Theory]
        [InlineData(2, 19)]
        [InlineData(3, 21)]
        [InlineData(4, 23)]
        public async Task ExecuteTaxation_CapAt15Flags(int factoryCount, int expectedTax)
        {
            var dbName = Guid.NewGuid().ToString();
            var gId = Guid.Empty;
            var uId = "";

            using (var context = GetDbContext(dbName))
            {
                var (gameId, userId, _) = await SetupGame(context, rondelPosition: 0);
                gId = gameId;
                uId = userId;

                var game = await context.Games.Include(g => g.NationStates).FirstAsync(g => g.Id == gameId);
                game.CurrentTurnNation = Nation.Russia;
                var russiaState = game.NationStates.First(n => n.Nation == Nation.Russia);
                russiaState.ControllerId = game.Players.First(p => p.UserId == userId).Id;

                var tsList = new List<TerritoryState>();
                foreach (var tDef in Imperial2030.Shared.Constants.TerritoryData.AllTerritories)
                {
                    var ts = new TerritoryState { GameId = gameId, TerritoryId = tDef.Id, Controller = null, HasFactory = false };
                    context.TerritoryStates.Add(ts);
                    tsList.Add(ts);
                }

                var factories = new[] { "Moscow", "Vladivostok", "Murmansk", "Novosibirsk" };
                for (int i = 0; i < factoryCount; i++)
                {
                    var ts = tsList.FirstOrDefault(t => t.TerritoryId == factories[i]);
                    if (ts != null) ts.HasFactory = true;
                }

                var controlledTerritories = new[] { 
                    "Switzerland", "Ukraine", "Korea", "Mongolia", "Kazakhstan",
                    "Japan", "Turkey", "Guinea", "Quebec", "Mexico",
                    "Colombia", "Afghanistan", "Alaska", "Canada",
                    "NorthAtlantic", "SouthAtlantic", "IndianOcean", "MediterraneanSea", "PacificOcean"
                };
                
                foreach (var territory in controlledTerritories)
                {
                    var ts = tsList.FirstOrDefault(t => t.TerritoryId == territory);
                    if (ts != null) ts.Controller = Nation.Russia;
                }
                
                await context.SaveChangesAsync();
            }

            // ACT & ASSERT in a fresh context to avoid EF Core tracking anomalies
            using (var verifyContext = GetDbContext(dbName))
            {
                var controller = GetController(verifyContext, uId);
                var result = await controller.ExecuteTaxation(gId);
                Assert.IsType<OkObjectResult>(result);
                
                var updatedRussia = await verifyContext.NationStates.FirstAsync(n => n.GameId == gId && n.Nation == Nation.Russia);
                Assert.Equal(expectedTax, updatedRussia.TaxRevenue);
            }
        }

        [Fact]
        public async Task StartGame_DoubleCall_DoesNotDuplicateEntities()
        {
            var dbName = Guid.NewGuid().ToString();
            Guid gameId;
            string hostUserId = "host-user";

            using (var context = GetDbContext(dbName))
            {
                gameId = Guid.NewGuid();
                var game = new Game
                {
                    Id = gameId,
                    Status = GameStatus.Lobby
                };
                var p1 = new Player { Id = Guid.NewGuid(), GameId = gameId, UserId = hostUserId, IsHost = true };
                var p2 = new Player { Id = Guid.NewGuid(), GameId = gameId, UserId = "other-user", IsHost = false };
                
                context.Games.Add(game);
                context.Players.AddRange(p1, p2);
                await context.SaveChangesAsync();
            }

            // Act: Call StartGame sequentially but simulating concurrent calls where both passed the initial Lobby check, 
            // OR simply call it twice on the same game to ensure idempotency if that's how we fix it.
            // Actually, we'll just call it twice. If it's fixed, the second call should return BadRequest or just not duplicate.
            using (var context1 = GetDbContext(dbName))
            using (var context2 = GetDbContext(dbName))
            {
                var controller1 = GetController(context1, hostUserId);
                var controller2 = GetController(context2, hostUserId);

                // Simulate concurrent calls: wait for both to finish
                var t1 = controller1.StartGame(gameId);
                var t2 = controller2.StartGame(gameId);
                await Task.WhenAll(t1, t2);
            }

            using (var context = GetDbContext(dbName))
            {
                var nationStatesCount = await context.NationStates.CountAsync(ns => ns.GameId == gameId);
                // There should be exactly 6 nations in a game, not 12.
                Assert.Equal(6, nationStatesCount);
            }
        }
    }
}
