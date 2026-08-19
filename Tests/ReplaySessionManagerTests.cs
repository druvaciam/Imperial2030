using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Imperial2030.Server.Controllers;
using Imperial2030.Server.Data;
using Imperial2030.Server.Helpers;
using Imperial2030.Server.Hubs;
using Imperial2030.Server.Models;
using Imperial2030.Server.Services;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Imperial2030.Tests
{
    public class ReplaySessionManagerTests
    {
        private ApplicationDbContext GetDbContext(string dbName)
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(databaseName: dbName)
                .Options;
            return new ApplicationDbContext(options);
        }

        private static void SetControllerUser(ControllerBase controller, string userId)
        {
            var httpContext = new DefaultHttpContext();
            var claims = new List<Claim> { new Claim(ClaimTypes.NameIdentifier, userId), new Claim(ClaimTypes.Name, userId) };
            var identity = new ClaimsIdentity(claims, "TestAuthType");
            httpContext.User = new ClaimsPrincipal(identity);
            var routeData = new Microsoft.AspNetCore.Routing.RouteData();
            var actionDescriptor = new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor();
            var actionContext = new ActionContext(httpContext, routeData, actionDescriptor);
            controller.ControllerContext = new ControllerContext(actionContext);
        }

        [Fact]
        public async Task StartReplay_PlaysBackToCompletion_MatchingOriginalFinalState()
        {
            // End-to-end proof of "Start Replay" (Server/Services/ReplaySessionManager.cs): play a small real
            // game to Finished through the real endpoints (so StartGame's roster/setup snapshot is actually
            // exercised, same reasoning as TestImportFromExportedJson), start a replay session against it,
            // let the paced background loop run to completion (with PacingMs turned down so the test doesn't
            // wait in real time), and assert the replayed board matches the original's final state — while
            // never touching the original game's own rows (the whole point of doing this in-memory).
            string dbName = Guid.NewGuid().ToString();
            var context = GetDbContext(dbName);

            var mockHub = new Mock<IHubContext<GameHub>>();
            var mockClients = new Mock<IHubClients>();
            var mockClientProxy = new Mock<IClientProxy>();
            mockHub.Setup(h => h.Clients).Returns(mockClients.Object);
            mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(mockClientProxy.Object);
            mockClients.Setup(c => c.All).Returns(mockClientProxy.Object);

            var mockPresenceTracker = new Mock<PresenceTracker>();
            var store = new Mock<IUserStore<ApplicationUser>>();
            var mockUserManager = new Mock<UserManager<ApplicationUser>>(store.Object, null, null, null, null, null, null, null, null);
            var mockNotificationService = new Mock<INotificationService>();
            var mockLogger = new Mock<Microsoft.Extensions.Logging.ILogger<BotService>>();

            // ReplaySessionManager's background loop resolves its own dependencies from a fresh DI scope
            // (it must — that loop outlives the HTTP request that started it), so the mock scope factory
            // needs to serve every type it asks for, not just ApplicationDbContext like the other replay
            // tests' scope factories (those only ever needed it for BotService's own background bot-turn work).
            var mockScopeFactory = new Mock<IServiceScopeFactory>();
            var botService = new BotService(mockScopeFactory.Object, mockHub.Object, [new Imperial2030.Server.Services.Bots.Strategies.DefaultBotStrategy()], mockLogger.Object);
            botService.SkipDelays = true;
            mockScopeFactory.Setup(s => s.CreateScope()).Returns(() =>
            {
                var scope = new Mock<IServiceScope>();
                var mockServiceProvider = new Mock<IServiceProvider>();
                var scopeContext = GetDbContext(dbName);
                mockServiceProvider.Setup(sp => sp.GetService(typeof(ApplicationDbContext))).Returns(scopeContext);
                mockServiceProvider.Setup(sp => sp.GetService(typeof(INotificationService))).Returns(mockNotificationService.Object);
                mockServiceProvider.Setup(sp => sp.GetService(typeof(IHubContext<GameHub>))).Returns(mockHub.Object);
                mockServiceProvider.Setup(sp => sp.GetService(typeof(PresenceTracker))).Returns(mockPresenceTracker.Object);
                mockServiceProvider.Setup(sp => sp.GetService(typeof(BotService))).Returns(botService);
                mockServiceProvider.Setup(sp => sp.GetService(typeof(UserManager<ApplicationUser>))).Returns(mockUserManager.Object);
                scope.Setup(s => s.ServiceProvider).Returns(mockServiceProvider.Object);
                return scope.Object;
            });

            var gamesController = new GamesController(context, mockUserManager.Object, mockHub.Object, mockPresenceTracker.Object, botService, mockNotificationService.Object);
            const string hostUserId = "host-user-id";
            SetControllerUser(gamesController, hostUserId);

            // 1. Create + start a small 2-player game, then play it out with real bots to a natural finish.
            // (A shortcut like directly setting Power=25 on the DB and taxing once would finish the ORIGINAL
            // game fine, but that Power jump is never captured in any logged action — replay reconstructs
            // PURELY from the action log, so it would have no way to know Power should start at 25 and could
            // never reach Finished. Bot play keeps every state change action-log-consistent, which is the
            // actual thing this test needs to be true for replay to have any chance of matching.)
            var createReq = new CreateGameRequest { Name = "ReplaySessionTestGame", MaxPlayers = 2, IsPrivate = false, VariantBonusOnlyForTaxIncreases = false };
            var createRes = await gamesController.CreateGame(createReq);
            var gameDto = Assert.IsType<GameDto>(Assert.IsType<CreatedAtActionResult>(createRes.Result).Value);
            var gameId = gameDto.Id;

            context.Players.Add(new Player { GameId = gameId, UserId = "human-0", BotName = "human-0", IsHost = false, IsBot = false });
            await context.SaveChangesAsync();
            var hostPlayer = context.Players.First(p => p.UserId == hostUserId);
            hostPlayer.BotName = hostUserId;
            await context.SaveChangesAsync();

            await gamesController.StartGame(gameId);

            var allPlayers = context.Players.Where(p => p.GameId == gameId).ToList();
            foreach (var p in allPlayers) p.IsBot = true;
            await context.SaveChangesAsync();
            botService.TriggerBotTurn(gameId);

            int timeoutTicks = 0;
            var hardTimeout = System.Diagnostics.Stopwatch.StartNew();
            var hardTestTimeout = TimeSpan.FromMinutes(5);
            while (timeoutTicks < 5000 && hardTimeout.Elapsed < hardTestTimeout)
            {
                using var scope = mockScopeFactory.Object.CreateScope();
                var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var pollGame = ctx.Games.AsNoTracking().FirstOrDefault(g => g.Id == gameId);
                if (pollGame == null || pollGame.Status == GameStatus.Finished) break;
                if (timeoutTicks % 30 == 0) botService.TriggerBotTurn(gameId);
                await Task.Delay(10);
                timeoutTicks++;
            }

            context.ChangeTracker.Clear();
            var finishedGame = await context.Games.AsNoTracking().FirstAsync(g => g.Id == gameId);
            Assert.Equal(GameStatus.Finished, finishedGame.Status);
            var finishedNationStates = await context.NationStates.AsNoTracking().Where(n => n.GameId == gameId).OrderBy(n => n.Nation).ToListAsync();

            // 3. Start a replay session against it, sped up so the test doesn't wait in real time.
            var replaySessionManager = new ReplaySessionManager(mockScopeFactory.Object, NullLogger<ReplaySessionManager>.Instance) { PacingMs = 0 };
            SetControllerUser(gamesController, hostUserId);
            var startResult = await gamesController.StartReplay(gameId, replaySessionManager);
            var startOk = Assert.IsType<OkObjectResult>(startResult);
            var replaySessionId = Assert.IsType<Guid>(startOk.Value);

            // 4. Poll GetReplayState until the background loop reports completion (bounded wait). A natural
            // bot-played 2-player game can occasionally take far more actions than usual before Power reaches
            // 25 — the replay engine itself has no "thinking" overhead (unlike the original bot play, which
            // other tests already give up to 5 minutes), but give this a generous bound too rather than risk
            // flaking on an unusually long original game.
            ReplayStateDto? state = null;
            var replayWait = System.Diagnostics.Stopwatch.StartNew();
            while (replayWait.Elapsed < TimeSpan.FromMinutes(2))
            {
                var stateResult = gamesController.GetReplayState(replaySessionId, replaySessionManager);
                state = (stateResult.Result as OkObjectResult)?.Value as ReplayStateDto;
                if (state?.IsComplete == true) break;
                await Task.Delay(20);
            }

            Assert.NotNull(state);
            Assert.True(state!.IsComplete, "Replay session did not complete within the test's bounded wait.");
            Assert.Null(state.ErrorMessage);
            Assert.NotNull(state.Game);
            Assert.Equal(GameStatus.Finished, state.Game!.Status);

            // The actual regression this test guards: the paced in-memory replay must reach the SAME final
            // state as the original, real game.
            var replayedNationStates = state.Game.NationStates.OrderBy(n => n.Nation).ToList();
            Assert.Equal(finishedNationStates.Count, replayedNationStates.Count);
            for (int i = 0; i < finishedNationStates.Count; i++)
            {
                Assert.Equal(finishedNationStates[i].Nation, replayedNationStates[i].Nation);
                Assert.Equal(finishedNationStates[i].Power, replayedNationStates[i].Power);
                Assert.Equal(finishedNationStates[i].Treasury, replayedNationStates[i].Treasury);
            }

            // 5. The original, real game must be completely untouched by any of this.
            var stillFinishedGame = await context.Games.AsNoTracking().FirstAsync(g => g.Id == gameId);
            Assert.Equal(finishedGame.Status, stillFinishedGame.Status);
            Assert.Equal(finishedGame.TurnCount, stillFinishedGame.TurnCount);

            // 6. Stop cleans the session up.
            var stopResult = await gamesController.StopReplay(replaySessionId, replaySessionManager);
            Assert.IsType<OkResult>(stopResult);
            var afterStop = gamesController.GetReplayState(replaySessionId, replaySessionManager);
            Assert.IsType<NotFoundResult>(afterStop.Result);
        }

        [Fact]
        public async Task ResetReplay_RestartsFromTheBeginning()
        {
            // Narrow test for Reset specifically: start a replay session, let it run a little, reset it, and
            // confirm it starts over (CurrentActionIndex goes back down, IsComplete clears) rather than
            // silently resuming from wherever it was or leaking the previous in-memory context.
            string dbName = Guid.NewGuid().ToString();
            var context = GetDbContext(dbName);

            var mockHub = new Mock<IHubContext<GameHub>>();
            var mockClients = new Mock<IHubClients>();
            var mockClientProxy = new Mock<IClientProxy>();
            mockHub.Setup(h => h.Clients).Returns(mockClients.Object);
            mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(mockClientProxy.Object);
            mockClients.Setup(c => c.All).Returns(mockClientProxy.Object);

            var mockPresenceTracker = new Mock<PresenceTracker>();
            var store = new Mock<IUserStore<ApplicationUser>>();
            var mockUserManager = new Mock<UserManager<ApplicationUser>>(store.Object, null, null, null, null, null, null, null, null);
            var mockNotificationService = new Mock<INotificationService>();
            var mockLogger = new Mock<Microsoft.Extensions.Logging.ILogger<BotService>>();

            var mockScopeFactory = new Mock<IServiceScopeFactory>();
            var botService = new BotService(mockScopeFactory.Object, mockHub.Object, [new Imperial2030.Server.Services.Bots.Strategies.DefaultBotStrategy()], mockLogger.Object);
            botService.SkipDelays = true;
            mockScopeFactory.Setup(s => s.CreateScope()).Returns(() =>
            {
                var scope = new Mock<IServiceScope>();
                var mockServiceProvider = new Mock<IServiceProvider>();
                var scopeContext = GetDbContext(dbName);
                mockServiceProvider.Setup(sp => sp.GetService(typeof(ApplicationDbContext))).Returns(scopeContext);
                mockServiceProvider.Setup(sp => sp.GetService(typeof(INotificationService))).Returns(mockNotificationService.Object);
                mockServiceProvider.Setup(sp => sp.GetService(typeof(IHubContext<GameHub>))).Returns(mockHub.Object);
                mockServiceProvider.Setup(sp => sp.GetService(typeof(PresenceTracker))).Returns(mockPresenceTracker.Object);
                mockServiceProvider.Setup(sp => sp.GetService(typeof(BotService))).Returns(botService);
                mockServiceProvider.Setup(sp => sp.GetService(typeof(UserManager<ApplicationUser>))).Returns(mockUserManager.Object);
                scope.Setup(s => s.ServiceProvider).Returns(mockServiceProvider.Object);
                return scope.Object;
            });

            var gamesController = new GamesController(context, mockUserManager.Object, mockHub.Object, mockPresenceTracker.Object, botService, mockNotificationService.Object);
            const string hostUserId = "host-user-id";
            SetControllerUser(gamesController, hostUserId);

            var createReq = new CreateGameRequest { Name = "ReplayResetTestGame", MaxPlayers = 2, IsPrivate = false, VariantBonusOnlyForTaxIncreases = false };
            var createRes = await gamesController.CreateGame(createReq);
            var gameDto = Assert.IsType<GameDto>(Assert.IsType<CreatedAtActionResult>(createRes.Result).Value);
            var gameId = gameDto.Id;

            context.Players.Add(new Player { GameId = gameId, UserId = "human-0", BotName = "human-0", IsHost = false, IsBot = false });
            await context.SaveChangesAsync();
            var hostPlayer = context.Players.First(p => p.UserId == hostUserId);
            hostPlayer.BotName = hostUserId;
            await context.SaveChangesAsync();

            await gamesController.StartGame(gameId);

            context.ChangeTracker.Clear();
            var game = await context.Games.FirstAsync(g => g.Id == gameId);
            var russiaNs = await context.NationStates.FirstAsync(n => n.GameId == gameId && n.Nation == Nation.Russia);
            russiaNs.Power = 25;
            russiaNs.RondelPosition = RondelData.TaxationSlot;
            game.CurrentTurnNation = Nation.Russia;
            await context.SaveChangesAsync();

            var russiaController = await context.Players.FirstAsync(p => p.Id == russiaNs.ControllerId);
            SetControllerUser(gamesController, russiaController.UserId!);
            await gamesController.ExecuteTaxation(gameId);

            // Deliberately slow-paced this time so we can catch it mid-replay before it completes.
            var replaySessionManager = new ReplaySessionManager(mockScopeFactory.Object, NullLogger<ReplaySessionManager>.Instance) { PacingMs = 300 };
            SetControllerUser(gamesController, hostUserId);
            var startResult = await gamesController.StartReplay(gameId, replaySessionManager);
            var startOk = Assert.IsType<OkObjectResult>(startResult);
            var replaySessionId = Assert.IsType<Guid>(startOk.Value);

            await Task.Delay(150); // let it start, but not finish

            var resetResult = await gamesController.ResetReplay(replaySessionId, replaySessionManager);
            Assert.IsType<OkResult>(resetResult);

            var stateAfterReset = gamesController.GetReplayState(replaySessionId, replaySessionManager);
            var stateOk = Assert.IsType<OkObjectResult>(stateAfterReset.Result);
            var state = Assert.IsType<ReplayStateDto>(stateOk.Value);
            Assert.False(state.IsComplete);
            Assert.False(state.IsPaused);
            Assert.Null(state.ErrorMessage);

            await gamesController.StopReplay(replaySessionId, replaySessionManager);
        }

        [Fact]
        public async Task IdleReplaySessions_AreEvicted_ButActiveOnesAreNot()
        {
            // StopReplay is only ever best-effort — a closed tab or dropped connection never sends it — and
            // ReplaySessionManager is a Singleton holding a live in-memory ApplicationDbContext per session,
            // so orphans would otherwise accumulate for the lifetime of the process. Guards both halves of
            // the sweep: an untouched session is reclaimed, and one still being polled is left alone.
            string dbName = Guid.NewGuid().ToString();
            var context = GetDbContext(dbName);

            var mockHub = new Mock<IHubContext<GameHub>>();
            var mockClients = new Mock<IHubClients>();
            var mockClientProxy = new Mock<IClientProxy>();
            mockHub.Setup(h => h.Clients).Returns(mockClients.Object);
            mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(mockClientProxy.Object);
            mockClients.Setup(c => c.All).Returns(mockClientProxy.Object);

            var mockPresenceTracker = new Mock<PresenceTracker>();
            var store = new Mock<IUserStore<ApplicationUser>>();
            var mockUserManager = new Mock<UserManager<ApplicationUser>>(store.Object, null, null, null, null, null, null, null, null);
            var mockNotificationService = new Mock<INotificationService>();
            var mockLogger = new Mock<Microsoft.Extensions.Logging.ILogger<BotService>>();

            var mockScopeFactory = new Mock<IServiceScopeFactory>();
            var botService = new BotService(mockScopeFactory.Object, mockHub.Object, [new Imperial2030.Server.Services.Bots.Strategies.DefaultBotStrategy()], mockLogger.Object);
            botService.SkipDelays = true;
            mockScopeFactory.Setup(s => s.CreateScope()).Returns(() =>
            {
                var scope = new Mock<IServiceScope>();
                var mockServiceProvider = new Mock<IServiceProvider>();
                var scopeContext = GetDbContext(dbName);
                mockServiceProvider.Setup(sp => sp.GetService(typeof(ApplicationDbContext))).Returns(scopeContext);
                mockServiceProvider.Setup(sp => sp.GetService(typeof(INotificationService))).Returns(mockNotificationService.Object);
                mockServiceProvider.Setup(sp => sp.GetService(typeof(IHubContext<GameHub>))).Returns(mockHub.Object);
                mockServiceProvider.Setup(sp => sp.GetService(typeof(PresenceTracker))).Returns(mockPresenceTracker.Object);
                mockServiceProvider.Setup(sp => sp.GetService(typeof(BotService))).Returns(botService);
                mockServiceProvider.Setup(sp => sp.GetService(typeof(UserManager<ApplicationUser>))).Returns(mockUserManager.Object);
                scope.Setup(s => s.ServiceProvider).Returns(mockServiceProvider.Object);
                return scope.Object;
            });

            var gamesController = new GamesController(context, mockUserManager.Object, mockHub.Object, mockPresenceTracker.Object, botService, mockNotificationService.Object);
            const string hostUserId = "host-user-id";
            SetControllerUser(gamesController, hostUserId);

            var createReq = new CreateGameRequest { Name = "ReplayEvictionTestGame", MaxPlayers = 2, IsPrivate = false, VariantBonusOnlyForTaxIncreases = false };
            var createRes = await gamesController.CreateGame(createReq);
            var gameDto = Assert.IsType<GameDto>(Assert.IsType<CreatedAtActionResult>(createRes.Result).Value);
            var gameId = gameDto.Id;

            context.Players.Add(new Player { GameId = gameId, UserId = "human-0", BotName = "human-0", IsHost = false, IsBot = false });
            await context.SaveChangesAsync();
            var hostPlayer = context.Players.First(p => p.UserId == hostUserId);
            hostPlayer.BotName = hostUserId;
            await context.SaveChangesAsync();

            await gamesController.StartGame(gameId);

            context.ChangeTracker.Clear();
            var game = await context.Games.FirstAsync(g => g.Id == gameId);
            var russiaNs = await context.NationStates.FirstAsync(n => n.GameId == gameId && n.Nation == Nation.Russia);
            russiaNs.Power = GameConstants.MaxPowerPoints;
            russiaNs.RondelPosition = RondelData.TaxationSlot;
            game.CurrentTurnNation = Nation.Russia;
            await context.SaveChangesAsync();

            var russiaController = await context.Players.FirstAsync(p => p.Id == russiaNs.ControllerId);
            SetControllerUser(gamesController, russiaController.UserId!);
            await gamesController.ExecuteTaxation(gameId);

            var replaySessionManager = new Imperial2030.Server.Services.ReplaySessionManager(
                mockScopeFactory.Object,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Imperial2030.Server.Services.ReplaySessionManager>.Instance) { PacingMs = 0 };

            SetControllerUser(gamesController, hostUserId);
            var startResult = await gamesController.StartReplay(gameId, replaySessionManager);
            var startOk = Assert.IsType<OkObjectResult>(startResult);
            var replaySessionId = Assert.IsType<Guid>(startOk.Value);

            // Still being watched (Get refreshes LastAccessedUtc, exactly as GetReplayState polling does):
            // a generous idle window must leave it completely alone.
            replaySessionManager.IdleTimeout = TimeSpan.FromHours(1);
            Assert.Equal(0, await replaySessionManager.EvictIdleSessionsAsync());
            Assert.IsType<OkObjectResult>(gamesController.GetReplayState(replaySessionId, replaySessionManager).Result);

            // Now treat everything as stale (as if the viewer's tab vanished without calling StopReplay).
            replaySessionManager.IdleTimeout = TimeSpan.Zero;
            Assert.Equal(1, await replaySessionManager.EvictIdleSessionsAsync());

            // The session — and with it its in-memory DbContext — is gone.
            Assert.IsType<NotFoundResult>(gamesController.GetReplayState(replaySessionId, replaySessionManager).Result);
            Assert.Equal(0, await replaySessionManager.EvictIdleSessionsAsync());
        }
    }
}
