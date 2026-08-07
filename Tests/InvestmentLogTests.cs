using System;
using System.Linq;
using System.Text.Json;
using Imperial2030.Server.Data;
using Imperial2030.Server.Helpers;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Imperial2030.Tests
{
    public class InvestmentLogTests
    {

        [Fact]
        public void LogInvestmentBuy_SerializesMetadataCorrectly_ForControlChange()
        {
            // Arrange
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(databaseName: "Test_LogInvestmentBuy")
                .Options;

            using var context = new ApplicationDbContext(options);

            var game = new Game
            {
                Id = Guid.NewGuid(),
                Status = GameStatus.InProgress
            };

            // Pass the actual metadata object used in BotService or GamesController
            var metadata = new InvestmentMetadata
            {
                NewControllerName = "Bot Bravo (RL-2)",
                OldControllerName = (string?)null,
                IsSwissBankKicked = false,
                Nation = Nation.India.ToString()
            };

            // Act
            GameLogger.LogInvestmentBuy(context, game, Nation.India, 20, "Bot Bravo (RL-2)", metadata);

            context.SaveChanges();

            // Assert
            Assert.Single(context.GameActions);
            var action = context.GameActions.First();
            Assert.Equal("Investment", action.ActionType);
            Assert.Equal("Bot Bravo (RL-2)", action.PlayerName);
            Assert.NotNull(action.Metadata);

            // Verify Client-side deserialization compatibility
            var meta = JsonSerializer.Deserialize<InvestmentMetadata>(action.Metadata, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            Assert.NotNull(meta);
            Assert.Equal("Bot Bravo (RL-2)", meta.NewControllerName);
            Assert.Null(meta.OldControllerName);
            Assert.False(meta.IsSwissBankKicked);
            Assert.Equal("India", meta.Nation);
        }
    }
}
