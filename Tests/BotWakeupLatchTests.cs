using System;
using System.Threading.Tasks;
using Imperial2030.Server.Data;
using Imperial2030.Server.Models;
using Imperial2030.Server.Services;
using Imperial2030.Shared.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Imperial2030.Tests
{
    /// <summary>
    /// Bots have no clock — they act only when TriggerBotTurn tells them something changed. Only one bot
    /// loop may run per game, and a caller that finds one already running returns immediately. That is
    /// correct while the running loop is still working, but not while it is on its way out: a loop that
    /// has just decided it has nothing left to do still holds the slot for a moment, so a request landing
    /// in that window used to be discarded by the caller and never seen by the loop. Nobody running,
    /// nobody coming — the game sits waiting on a bot move that never happens.
    /// </summary>
    public class BotWakeupLatchTests
    {
        private static BotService BuildBotService(string dbName)
        {
            var hub = new Mock<IHubContext<Imperial2030.Server.Hubs.GameHub>>();
            var clients = new Mock<IHubClients>();
            hub.Setup(h => h.Clients).Returns(clients.Object);
            clients.Setup(c => c.Group(It.IsAny<string>())).Returns(new Mock<IClientProxy>().Object);

            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(dbName).Options;
            var scopeFactory = new Mock<IServiceScopeFactory>();
            scopeFactory.Setup(f => f.CreateScope()).Returns(() =>
            {
                var scope = new Mock<IServiceScope>();
                var sp = new Mock<IServiceProvider>();
                sp.Setup(p => p.GetService(typeof(ApplicationDbContext))).Returns(new ApplicationDbContext(options));
                scope.Setup(x => x.ServiceProvider).Returns(sp.Object);
                return scope.Object;
            });

            return new BotService(scopeFactory.Object, hub.Object,
                [new Imperial2030.Server.Services.Bots.Strategies.DefaultBotStrategy()],
                new Mock<ILogger<BotService>>().Object) { SkipDelays = true };
        }

        [Fact]
        public async Task ATriggerArrivingWhileALoopIsRunning_IsRecordedRatherThanDropped()
        {
            var gameId = Guid.NewGuid();
            var botService = BuildBotService(Guid.NewGuid().ToString());

            // Stand in for a loop that is already running: the slot is taken.
            Assert.True(BotService.TryClaimBotLoopSlot(gameId));
            try
            {
                // A move has just been made that needs a bot. This call finds the slot taken and returns
                // straight away - the whole question is whether it leaves anything behind when it does.
                await botService.TryPlayBotTurnAsync(gameId);

                Assert.True(BotService.HasPendingWakeup(gameId),
                    "The request was dropped: a loop on its way out will never see it, and nothing else will ask again.");
            }
            finally
            {
                BotService.ReleaseBotLoopSlot(gameId);
            }
        }

        [Fact]
        public async Task ACompletedLoop_LeavesNoWakeupBehind()
        {
            // The other half: the mark must be consumed by the pass that services it, or every finished
            // loop would immediately re-trigger itself forever.
            var dbName = Guid.NewGuid().ToString();
            var gameId = Guid.NewGuid();

            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(dbName).Options;
            using (var seed = new ApplicationDbContext(options))
            {
                // Finished, so the loop has nothing to do and exits on its first pass.
                seed.Games.Add(new Game { Id = gameId, Name = "Idle", Status = GameStatus.Finished });
                await seed.SaveChangesAsync();
            }

            var botService = BuildBotService(dbName);

            await botService.TryPlayBotTurnAsync(gameId);

            Assert.False(BotService.HasPendingWakeup(gameId));
            Assert.True(BotService.TryClaimBotLoopSlot(gameId), "The bot-loop slot was not released.");
            BotService.ReleaseBotLoopSlot(gameId);
        }
    }
}
