using Imperial2030.Server.Models;
using Imperial2030.Shared.Models;
using Xunit;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Imperial2030.Tests
{
    public class TieBreakerTests
    {
        [Fact]
        public void TestTieBreaker_HigherCreditSumInMostPowerfulNation_Wins()
        {
            // Arrange
            var game = new Game();
            
            var p1 = new Player { Id = Guid.NewGuid(), Cash = 10, BotName = "Player1" };
            var p2 = new Player { Id = Guid.NewGuid(), Cash = 10, BotName = "Player2" };
            
            game.Players = new List<Player> { p1, p2 };
            
            // Russia has most power (15 -> factor 3)
            // China has second most (10 -> factor 2)
            game.NationStates = new List<NationState>
            {
                new NationState { Nation = Nation.Russia, Power = 15 }, 
                new NationState { Nation = Nation.China, Power = 10 }
            };
            
            // Player 1 has 12 cost bond in Russia (interest 5)
            // Player 2 has 20 cost bond in China (interest 7), plus cash
            // We want base score to be perfectly tied.
            // P1 Score: 10 + (5 * 3) = 25
            // P2 Score: 11 + (7 * 2) = 25
            p2.Cash = 11;
            
            game.Bonds = new List<Bond>
            {
                new Bond { Nation = Nation.Russia, Cost = 12, Interest = 5, HolderId = p1.Id },
                new Bond { Nation = Nation.China, Cost = 20, Interest = 7, HolderId = p2.Id }
            };

            // Act
            var ranked = game.GetRankedPlayers();

            // Assert
            Assert.Equal(2, ranked.Count);
            // Tie breaker goes to credit sum in Russia (since Russia is highest power).
            // P1 credit sum in Russia = 12
            // P2 credit sum in Russia = 0
            // P1 should win
            Assert.Equal(p1.Id, ranked[0].Id);
            
            // Now change P2 to have 16 cost bond in Russia (interest 6)
            // P2 Score: 7 + (6 * 3) = 25
            p2.Cash = 7;
            var bondToRemove = game.Bonds.Last();
            game.Bonds.Remove(bondToRemove);
            game.Bonds.Add(new Bond { Nation = Nation.Russia, Cost = 16, Interest = 6, HolderId = p2.Id });
            
            // Act
            ranked = game.GetRankedPlayers();
            
            // Assert
            // P2 credit sum in Russia = 16
            // P1 credit sum in Russia = 12
            // P2 should win
            Assert.Equal(p2.Id, ranked[0].Id);
        }
        
        [Fact]
        public void TestTieBreaker_TiedInFirstNation_ResolvedInSecondNation()
        {
            // Arrange
            var game = new Game();
            
            var p1 = new Player { Id = Guid.NewGuid(), Cash = 10 };
            var p2 = new Player { Id = Guid.NewGuid(), Cash = 10 };
            
            game.Players = new List<Player> { p1, p2 };
            
            game.NationStates = new List<NationState>
            {
                new NationState { Nation = Nation.Russia, Power = 15 }, 
                new NationState { Nation = Nation.China, Power = 10 }
            };
            
            // Both have 12 cost in Russia (interest 5). Score: 10 + 15 = 25
            // Both have tied credit sum in Russia (12).
            // P1 has 6 cost in China (interest 3). Score: 25 + 6 = 31
            // P2 has 9 cost in China (interest 4). Wait, if P2 has 9 cost, score is different.
            // Let's adjust cash so scores are tied.
            // P1: 10 cash, Russia 12(int 5), China 6(int 3). Score: 10 + 15 + 6 = 31
            // P2: 8 cash, Russia 12(int 5), China 9(int 4). Score: 8 + 15 + 8 = 31
            
            p2.Cash = 8;
            
            game.Bonds = new List<Bond>
            {
                new Bond { Nation = Nation.Russia, Cost = 12, Interest = 5, HolderId = p1.Id },
                new Bond { Nation = Nation.Russia, Cost = 12, Interest = 5, HolderId = p2.Id },
                new Bond { Nation = Nation.China, Cost = 6, Interest = 3, HolderId = p1.Id },
                new Bond { Nation = Nation.China, Cost = 9, Interest = 4, HolderId = p2.Id }
            };

            // Act
            var ranked = game.GetRankedPlayers();

            // Assert
            // Tie breaker goes to China (P2 has 9, P1 has 6)
            Assert.Equal(p2.Id, ranked[0].Id);
        }
    }
}
