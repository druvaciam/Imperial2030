using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Imperial2030.Server.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Imperial2030.Tests
{
    /// <summary>
    /// TriggerBotTurn is fire-and-forget: nothing awaits the Task.Run it starts. An exception escaping it
    /// therefore becomes an UnobservedTaskException — surfaced only when the task is garbage collected,
    /// long after the fact, and by default swallowed entirely. It has to be observed at that boundary, and
    /// with the game it belongs to, or a bot that dies mid-turn looks exactly like a bot that had nothing
    /// to do.
    /// </summary>
    public class BotTurnFailureObservationTests
    {
        [Fact]
        public async Task TriggerBotTurn_WhenTheBackgroundTurnThrows_LogsTheFailureWithTheGameId()
        {
            var gameId = Guid.NewGuid();

            var hub = new Mock<IHubContext<Imperial2030.Server.Hubs.GameHub>>();
            var clients = new Mock<IHubClients>();
            hub.Setup(h => h.Clients).Returns(clients.Object);
            clients.Setup(c => c.Group(It.IsAny<string>())).Returns(new Mock<IClientProxy>().Object);

            // Guarantees the background turn blows up: resolving a DbContext is the first thing it does.
            var scopeFactory = new Mock<IServiceScopeFactory>();
            scopeFactory.Setup(f => f.CreateScope()).Throws(new InvalidOperationException("scope unavailable"));

            var logger = new Mock<ILogger<BotService>>();
            var loggedMessages = new System.Collections.Concurrent.ConcurrentBag<string>();
            logger.Setup(l => l.Log(
                    It.IsAny<LogLevel>(), It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                    It.IsAny<Exception>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
                .Callback(new InvocationAction(invocation =>
                {
                    var state = invocation.Arguments[2];
                    var ex = (Exception?)invocation.Arguments[3];
                    var formatter = invocation.Arguments[4];
                    var formatted = formatter.GetType().GetMethod("Invoke")!.Invoke(formatter, new[] { state, ex });
                    loggedMessages.Add(formatted?.ToString() ?? "");
                }));

            var botService = new BotService(scopeFactory.Object, hub.Object,
                [new Imperial2030.Server.Services.Bots.Strategies.DefaultBotStrategy()], logger.Object)
            { SkipDelays = true };

            botService.TriggerBotTurn(gameId, delayMs: 0);

            // The work is on a background task; give it a bounded window to land.
            var wait = Stopwatch.StartNew();
            while (loggedMessages.IsEmpty && wait.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(20);
            }

            Assert.Contains(loggedMessages, m => m.Contains(gameId.ToString(), StringComparison.OrdinalIgnoreCase));
        }
    }
}
