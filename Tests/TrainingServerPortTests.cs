using Imperial2030.Server.Services;
using Imperial2030.Server.Services.Bots;
using Imperial2030.Server.Services.Bots.Strategies;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Imperial2030.Tests;

/// <summary>
/// The training server listens on 5295 unless told otherwise, so a smoke run can be given its own
/// server while a real training run keeps the documented port (CLAUDE.md).
/// </summary>
public class TrainingServerPortTests
{
    private static TcpTrainingServer Build(params (string Key, string Value)[] settings)
    {
        var hub = new Mock<IHubContext<Imperial2030.Server.Hubs.GameHub>>();
        var clients = new Mock<IHubClients>();
        hub.Setup(h => h.Clients).Returns(clients.Object);
        var botService = new BotService(new Mock<IServiceScopeFactory>().Object, hub.Object,
            new List<IBotStrategy> { new DefaultBotStrategy() },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BotService>.Instance);

        IConfiguration? configuration = settings.Length == 0 ? null : new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        return new TcpTrainingServer(botService, new Mock<ILogger<TcpTrainingServer>>().Object, configuration);
    }

    [Fact]
    public void WithoutConfigurationItListensOnTheDocumentedPort()
    {
        Assert.Equal(5295, TcpTrainingServer.DefaultPort);
        Assert.Equal(TcpTrainingServer.DefaultPort, Build().Port);
        Assert.Equal(TcpTrainingServer.DefaultPort, Build(("Training:SomethingElse", "1")).Port);
    }

    [Fact]
    public void TrainingPortMovesTheListener()
    {
        // Set in the environment as Training__Port; the same key through any configuration source.
        Assert.Equal(5296, Build(("Training:Port", "5296")).Port);
    }
}
