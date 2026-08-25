using System;
using System.Threading.Tasks;
using Imperial2030.Server.Data;
using Imperial2030.Server.Hubs;
using Imperial2030.Server.Models;
using Imperial2030.Server.Services;
using Imperial2030.Shared.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Imperial2030.Tests
{
    /// <summary>
    /// GameHub.JoinGameGroup took an arbitrary string, never parsed it as a Guid and never checked the
    /// game existed, then used it as a permanent key in the PresenceTracker singleton. Any authenticated
    /// connection - guests included - could therefore grow the tracker without bound just by joining
    /// invented game ids.
    /// </summary>
    public class GameHubJoinValidationTests
    {
        private static (GameHub Hub, PresenceTracker Tracker) BuildHub(string dbName, string userId)
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(dbName).Options;

            var scopeFactory = new Mock<IServiceScopeFactory>();
            scopeFactory.Setup(f => f.CreateScope()).Returns(() =>
            {
                var scope = new Mock<IServiceScope>();
                var sp = new Mock<IServiceProvider>();
                sp.Setup(p => p.GetService(typeof(ApplicationDbContext))).Returns(new ApplicationDbContext(options));
                scope.Setup(s => s.ServiceProvider).Returns(sp.Object);
                return scope.Object;
            });

            var tracker = new PresenceTracker();

            var context = new Mock<HubCallerContext>();
            context.Setup(c => c.ConnectionId).Returns("conn-1");
            context.Setup(c => c.UserIdentifier).Returns(userId);

            var clients = new Mock<IHubCallerClients>();
            clients.Setup(c => c.Group(It.IsAny<string>())).Returns(new Mock<IClientProxy>().Object);
            clients.Setup(c => c.All).Returns(new Mock<IClientProxy>().Object);

            var hub = new GameHub(tracker, scopeFactory.Object)
            {
                Context = context.Object,
                Clients = clients.Object,
                Groups = new Mock<IGroupManager>().Object
            };

            return (hub, tracker);
        }

        [Fact]
        public async Task JoinGameGroup_WithAnIdThatIsNotAGuid_LeavesTheTrackerUntouched()
        {
            var (hub, tracker) = BuildHub(Guid.NewGuid().ToString(), "user-1");

            await hub.JoinGameGroup("../../etc/passwd", isObserver: true);
            await hub.JoinGameGroup("not-a-guid", isObserver: false);

            Assert.Equal(0, tracker.TrackedObserverGameCount);
            Assert.Equal(0, tracker.TrackedActivePlayerGameCount);
        }

        [Fact]
        public async Task JoinGameGroup_WithAWellFormedIdForAGameThatDoesNotExist_LeavesTheTrackerUntouched()
        {
            // A well-formed Guid is not enough on its own: an attacker can mint unlimited fresh ones, so
            // parsing alone would still allow unbounded growth.
            var (hub, tracker) = BuildHub(Guid.NewGuid().ToString(), "user-1");

            for (int i = 0; i < 5; i++)
            {
                await hub.JoinGameGroup(Guid.NewGuid().ToString(), isObserver: true);
            }

            Assert.Equal(0, tracker.TrackedObserverGameCount);
        }

        [Fact]
        public async Task JoinGameGroup_ForARealGame_StillRegistersPresence()
        {
            var dbName = Guid.NewGuid().ToString();
            var gameId = Guid.NewGuid();

            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(dbName).Options;
            using (var seed = new ApplicationDbContext(options))
            {
                seed.Games.Add(new Game { Id = gameId, Name = "Real", Status = GameStatus.Lobby });
                await seed.SaveChangesAsync();
            }

            var (hub, tracker) = BuildHub(dbName, "user-1");

            int observers = await hub.JoinGameGroup(gameId.ToString(), isObserver: true);

            Assert.Equal(1, observers);
            Assert.Equal(1, tracker.TrackedObserverGameCount);
            Assert.Equal(1, tracker.GetObserverCount(gameId.ToString()));
        }
    }
}
