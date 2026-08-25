using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Imperial2030.Server.Controllers;
using Imperial2030.Server.Data;
using Imperial2030.Server.Models;
using Imperial2030.Server.Services;
using Imperial2030.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Imperial2030.Tests
{
    /// <summary>
    /// PlayerHelper.GetPlayerName falls back to a synchronous `context.Users.FirstOrDefault(...)` whenever
    /// the Player's User navigation was not loaded. Endpoints that log an action resolve the acting
    /// player's display name through it, so a query shape that omits `.ThenInclude(p => p.User)` turns
    /// every logged move into an extra blocking round-trip.
    ///
    /// Run against real SQLite rather than the InMemory provider, because the whole point is to count the
    /// SQL that actually gets issued — InMemory emits none.
    /// </summary>
    public class PlayerNameQueryCountTests : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly List<string> _sqlLog = new();

        public PlayerNameQueryCountTests()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
        }

        public void Dispose() => _connection.Dispose();

        private ApplicationDbContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .LogTo(line => { lock (_sqlLog) _sqlLog.Add(line); }, LogLevel.Information)
                .Options;
            return new ApplicationDbContext(options);
        }

        private int UserTableQueryCount()
        {
            lock (_sqlLog)
            {
                return _sqlLog.Count(l => l.Contains("FROM \"AspNetUsers\"", StringComparison.Ordinal));
            }
        }

        private static ManeuverController BuildController(ApplicationDbContext context, string userId)
        {
            var hub = new Mock<IHubContext<Imperial2030.Server.Hubs.GameHub>>();
            var clients = new Mock<IHubClients>();
            hub.Setup(h => h.Clients).Returns(clients.Object);
            clients.Setup(c => c.Group(It.IsAny<string>())).Returns(new Mock<IClientProxy>().Object);

            var botService = new BotService(new Mock<IServiceScopeFactory>().Object, hub.Object,
                [new Imperial2030.Server.Services.Bots.Strategies.DefaultBotStrategy()],
                new Mock<ILogger<BotService>>().Object);

            var controller = new ManeuverController(context, hub.Object, botService);
            var httpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, userId), new Claim(ClaimTypes.Name, userId)], "TestAuthType"))
            };
            controller.ControllerContext = new ControllerContext(new ActionContext(httpContext,
                new Microsoft.AspNetCore.Routing.RouteData(),
                new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor()));
            return controller;
        }

        [Fact]
        public async Task ToggleHostility_ResolvesTheActingPlayersName_WithoutAnExtraUserQuery()
        {
            const string userId = "user-1";
            var gameId = Guid.NewGuid();
            var playerId = Guid.NewGuid();
            var unitId = Guid.NewGuid();

            using (var seed = CreateContext())
            {
                await seed.Database.EnsureCreatedAsync();

                seed.Users.Add(new ApplicationUser { Id = userId, UserName = "Alice", NormalizedUserName = "ALICE" });
                seed.Games.Add(new Game
                {
                    Id = gameId,
                    Name = "Name Query Game",
                    Status = GameStatus.InProgress,
                    CurrentTurnNation = Nation.Russia,
                    CurrentManeuverPhase = ManeuverPhase.Armies
                });
                seed.Players.Add(new Player { Id = playerId, GameId = gameId, UserId = userId });
                seed.NationStates.Add(new NationState { Nation = Nation.Russia, ControllerId = playerId, GameId = gameId });
                seed.Units.Add(new Unit { Id = unitId, GameId = gameId, Nation = Nation.Russia, UnitType = UnitType.Army, TerritoryId = "Moscow" });
                await seed.SaveChangesAsync();
            }

            using var context = CreateContext();
            var controller = BuildController(context, userId);

            lock (_sqlLog) _sqlLog.Clear();

            var result = await controller.ToggleHostility(gameId, unitId);
            Assert.IsType<OkResult>(result);

            // The name must still have been resolved correctly - this is not about skipping the lookup,
            // it is about the load already carrying it.
            var logged = await CreateContext().GameActions
                .Where(a => a.GameId == gameId && a.ActionType == "ToggleHostility")
                .Select(a => a.PlayerName)
                .FirstOrDefaultAsync();
            Assert.Equal("Alice", logged);

            Assert.Equal(0, UserTableQueryCount());
        }
    }
}
