using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Imperial2030.Server.Controllers;
using Imperial2030.Server.Data;
using Imperial2030.Server.Models;
using Imperial2030.Server.Services;
using Imperial2030.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Imperial2030.Tests
{
    /// <summary>
    /// Admission control for "Start Replay".
    ///
    /// POST /api/games/{id}/replay/start is [AllowAnonymous] by design (the Vue viewer prototype consumes
    /// it without auth), and every accepted call allocates a dedicated EF InMemory database, a long-lived
    /// DbContext, a background replay task and a full GameDetailDto snapshot, held until the idle sweep
    /// reclaims it. With no cap of any kind, an unauthenticated caller could allocate those in a loop and
    /// exhaust the server.
    /// </summary>
    public class ReplayAdmissionTests
    {
        private static ApplicationDbContext NewContext() =>
            new(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

        private static IServiceScopeFactory ScopeFactory(ApplicationDbContext context)
        {
            var hub = new Mock<IHubContext<Imperial2030.Server.Hubs.GameHub>>();
            var clients = new Mock<IHubClients>();
            hub.Setup(h => h.Clients).Returns(clients.Object);
            clients.Setup(c => c.Group(It.IsAny<string>())).Returns(new Mock<IClientProxy>().Object);
            clients.Setup(c => c.All).Returns(new Mock<IClientProxy>().Object);

            var scopeFactory = new Mock<IServiceScopeFactory>();
            var provider = new Mock<IServiceProvider>();
            var notifications = new Mock<INotificationService>();
            var userManager = new Mock<UserManager<ApplicationUser>>(
                new Mock<IUserStore<ApplicationUser>>().Object, null, null, null, null, null, null, null, null);
            var botService = new BotService(scopeFactory.Object, hub.Object,
                new List<Imperial2030.Server.Services.Bots.IBotStrategy>(), NullLogger<BotService>.Instance);

            provider.Setup(p => p.GetService(typeof(ApplicationDbContext))).Returns(context);
            provider.Setup(p => p.GetService(typeof(IHubContext<Imperial2030.Server.Hubs.GameHub>))).Returns(hub.Object);
            provider.Setup(p => p.GetService(typeof(PresenceTracker))).Returns(new PresenceTracker());
            provider.Setup(p => p.GetService(typeof(BotService))).Returns(botService);
            provider.Setup(p => p.GetService(typeof(INotificationService))).Returns(notifications.Object);
            provider.Setup(p => p.GetService(typeof(UserManager<ApplicationUser>))).Returns(userManager.Object);

            scopeFactory.Setup(s => s.CreateScope()).Returns(() =>
            {
                var scope = new Mock<IServiceScope>();
                scope.Setup(s => s.ServiceProvider).Returns(provider.Object);
                return scope.Object;
            });
            return scopeFactory.Object;
        }

        /// <summary>Smallest roster the replay seeder accepts: two players and one nation assignment.</summary>
        private static (Game Game, List<GameActionDto> Actions, GameSetupMetadata Setup) MinimalReplayInput()
        {
            var playerA = Guid.NewGuid();
            var playerB = Guid.NewGuid();
            var setup = new GameSetupMetadata
            {
                MaxPlayers = 2,
                NationDistribution = new Dictionary<Nation, Guid> { [Nation.Russia] = playerA },
                Players =
                {
                    new PlayerRosterEntry { PlayerId = playerA, IsHost = true, IsBot = true, BotName = "Bot A", DisplayName = "Bot A" },
                    new PlayerRosterEntry { PlayerId = playerB, IsBot = true, BotName = "Bot B", DisplayName = "Bot B" }
                }
            };

            var game = new Game { Id = Guid.NewGuid(), Name = "Admission", Status = GameStatus.Finished };

            // StartGame is on the replay skip-list, so the background loop completes almost immediately -
            // these tests are about admission, not playback.
            var actions = new List<GameActionDto>
            {
                new() { Id = Guid.NewGuid(), OrderIndex = 0, ActionType = "StartGame", PlayerName = "Bot A", Message = "start", Metadata = "{}" }
            };

            return (game, actions, setup);
        }

        private static ReplaySessionManager NewManager(ApplicationDbContext context, int globalCap, int perOwnerCap) =>
            new(ScopeFactory(context), NullLogger<ReplaySessionManager>.Instance)
            {
                PacingMs = 0,
                MaxConcurrentSessions = globalCap,
                MaxSessionsPerOwner = perOwnerCap
            };

        [Fact]
        public async Task StartReplay_BeyondTheGlobalCap_IsRejected()
        {
            using var context = NewContext();
            using var manager = NewManager(context, globalCap: 2, perOwnerCap: 10);

            for (int i = 1; i <= 2; i++)
            {
                var (game, actions, setup) = MinimalReplayInput();
                var accepted = await manager.StartReplayAsync(game, actions, setup, ownerKey: $"owner-{i}");
                Assert.Equal(ReplayAdmission.Accepted, accepted.Admission);
                Assert.NotNull(accepted.SessionId);
            }

            var (lastGame, lastActions, lastSetup) = MinimalReplayInput();
            var rejected = await manager.StartReplayAsync(lastGame, lastActions, lastSetup, ownerKey: "owner-3");

            Assert.Equal(ReplayAdmission.ServerAtCapacity, rejected.Admission);
            Assert.Null(rejected.SessionId);
        }

        /// <summary>
        /// Without a per-caller cap a single client could consume the entire global budget and lock every
        /// other viewer out, which is the same denial of service with extra steps.
        /// </summary>
        [Fact]
        public async Task StartReplay_BeyondThePerCallerCap_IsRejectedButOtherCallersStillGetIn()
        {
            using var context = NewContext();
            using var manager = NewManager(context, globalCap: 50, perOwnerCap: 2);

            for (int i = 1; i <= 2; i++)
            {
                var (game, actions, setup) = MinimalReplayInput();
                var accepted = await manager.StartReplayAsync(game, actions, setup, ownerKey: "greedy");
                Assert.Equal(ReplayAdmission.Accepted, accepted.Admission);
            }

            var (thirdGame, thirdActions, thirdSetup) = MinimalReplayInput();
            var rejected = await manager.StartReplayAsync(thirdGame, thirdActions, thirdSetup, ownerKey: "greedy");
            Assert.Equal(ReplayAdmission.CallerAtCapacity, rejected.Admission);

            var (otherGame, otherActions, otherSetup) = MinimalReplayInput();
            var otherCaller = await manager.StartReplayAsync(otherGame, otherActions, otherSetup, ownerKey: "someone-else");
            Assert.Equal(ReplayAdmission.Accepted, otherCaller.Admission);
        }

        [Fact]
        public async Task StoppingASession_ReleasesCapacity()
        {
            using var context = NewContext();
            using var manager = NewManager(context, globalCap: 1, perOwnerCap: 10);

            var (game, actions, setup) = MinimalReplayInput();
            var first = await manager.StartReplayAsync(game, actions, setup, ownerKey: "viewer");
            Assert.Equal(ReplayAdmission.Accepted, first.Admission);

            var (blockedGame, blockedActions, blockedSetup) = MinimalReplayInput();
            var blocked = await manager.StartReplayAsync(blockedGame, blockedActions, blockedSetup, ownerKey: "viewer");
            Assert.Equal(ReplayAdmission.ServerAtCapacity, blocked.Admission);

            Assert.True(await manager.StopAsync(first.SessionId!.Value));

            var (retryGame, retryActions, retrySetup) = MinimalReplayInput();
            var retry = await manager.StartReplayAsync(retryGame, retryActions, retrySetup, ownerKey: "viewer");
            Assert.Equal(ReplayAdmission.Accepted, retry.Admission);
        }

        /// <summary>
        /// The capacity check has to happen BEFORE the endpoint loads the source game and projects its
        /// entire action log into DTOs - otherwise every rejected request still costs a full multi-collection
        /// query plus thousands of allocations, and the cap protects memory while leaving the database wide
        /// open to exactly the same flood.
        ///
        /// Asserted by asking for a game that does not exist while at capacity: a 404 would prove the lookup
        /// ran first, 429 proves admission was decided before any of that work.
        /// </summary>
        [Fact]
        public async Task StartReplay_AtCapacity_RejectsBeforeTouchingTheDatabase()
        {
            using var context = NewContext();
            using var manager = NewManager(context, globalCap: 1, perOwnerCap: 10);

            var (game, actions, setup) = MinimalReplayInput();
            var first = await manager.StartReplayAsync(game, actions, setup, ownerKey: "1.2.3.4");
            Assert.Equal(ReplayAdmission.Accepted, first.Admission);

            var controller = BuildController(context, remoteIp: "1.2.3.4");

            var result = await controller.StartReplay(Guid.NewGuid(), manager);

            var status = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status429TooManyRequests, status.StatusCode);
        }

        private static GamesController BuildController(ApplicationDbContext context, string remoteIp)
        {
            var hub = new Mock<IHubContext<Imperial2030.Server.Hubs.GameHub>>();
            var clients = new Mock<IHubClients>();
            hub.Setup(h => h.Clients).Returns(clients.Object);
            clients.Setup(c => c.Group(It.IsAny<string>())).Returns(new Mock<IClientProxy>().Object);
            clients.Setup(c => c.All).Returns(new Mock<IClientProxy>().Object);

            var scopeFactory = new Mock<IServiceScopeFactory>();
            scopeFactory.Setup(s => s.CreateScope()).Returns(() =>
            {
                var scope = new Mock<IServiceScope>();
                scope.Setup(s => s.ServiceProvider).Returns(new Mock<IServiceProvider>().Object);
                return scope.Object;
            });

            var userManager = new Mock<UserManager<ApplicationUser>>(
                new Mock<IUserStore<ApplicationUser>>().Object, null, null, null, null, null, null, null, null);
            var botService = new BotService(scopeFactory.Object, hub.Object,
                new List<Imperial2030.Server.Services.Bots.IBotStrategy>(), NullLogger<BotService>.Instance);

            var controller = new GamesController(context, userManager.Object, hub.Object,
                new PresenceTracker(), botService, new Mock<INotificationService>().Object);

            var httpContext = new DefaultHttpContext();
            httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(remoteIp);
            controller.ControllerContext = new ControllerContext(
                new ActionContext(httpContext, new Microsoft.AspNetCore.Routing.RouteData(),
                    new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor()));
            return controller;
        }
    }
}
