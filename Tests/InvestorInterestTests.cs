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
using Moq;
using Xunit;

namespace Imperial2030.Tests
{
    public class InvestorInterestTests
    {
        private ApplicationDbContext GetDbContext(string dbName)
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(databaseName: dbName)
                .Options;
            return new ApplicationDbContext(options);
        }

        [Fact]
        public async Task TestLowestInterestPaidFirst_PartialPayment()
        {
            // Case 1: Treasury insufficient for others. Controller has 0 cash.
            // B has 2M interest. A has 5M interest.
            // Treasury is 4M.
            // Expected: B gets 2M. 2M remaining. A gets 2M (partial). Treasury = 0.
            
            string dbName = Guid.NewGuid().ToString();
            var context = GetDbContext(dbName);

            var controllerId = Guid.NewGuid();
            var playerAId = Guid.NewGuid();
            var playerBId = Guid.NewGuid();

            var controller = new Player { Id = controllerId, UserId = "user-c", Cash = 0 };
            var playerA = new Player { Id = playerAId, UserId = "user-a", Cash = 0 };
            var playerB = new Player { Id = playerBId, UserId = "user-b", Cash = 0 };

            var gameId = Guid.NewGuid();
            var game = new Game
            {
                Id = gameId,
                Name = "Test Game",
                Status = GameStatus.InProgress,
                CurrentTurnNation = Nation.Europe,
                Players = new List<Player> { controller, playerA, playerB },
                NationStates = new List<NationState>
                {
                    new NationState { Nation = Nation.Europe, ControllerId = controllerId, Treasury = 4, RondelPosition = 3, Power = 0 },
                },
                Bonds = new List<Bond>
                {
                    new Bond { Id = Guid.NewGuid(), Nation = Nation.Europe, Cost = 4, Interest = 2, HolderId = playerBId },
                    new Bond { Id = Guid.NewGuid(), Nation = Nation.Europe, Cost = 12, Interest = 5, HolderId = playerAId },
                    new Bond { Id = Guid.NewGuid(), Nation = Nation.Europe, Cost = 9, Interest = 4, HolderId = controllerId }
                }
            };
            context.Games.Add(game);
            await context.SaveChangesAsync();

            GamesController.HandleInvestorPhase(context, game, game.NationStates.First(), controller, isLandedOn: true);

            var dbPlayerA = await context.Players.FindAsync(playerAId);
            var dbPlayerB = await context.Players.FindAsync(playerBId);
            var dbController = await context.Players.FindAsync(controllerId);
            var dbNation = game.NationStates.First();

            Assert.Equal(2, dbPlayerB.Cash); // Full 2M
            Assert.Equal(2, dbPlayerA.Cash); // Partial 2M
            Assert.Equal(0, dbController.Cash); // Nothing
            Assert.Equal(0, dbNation.Treasury); // Empty
        }

        [Fact]
        public async Task TestControllerContributesCash()
        {
            // Case 2: Treasury insufficient for others. Controller has enough cash to help.
            // B has 2M interest. A has 5M interest. Owed to others = 7M.
            // Treasury is 4M. Deficit = 3M.
            // Controller has 5M cash.
            // Expected: Controller pays 3M. Treasury is 0. 
            // Total for others = 7M. B gets 2M, A gets 5M. 
            // Controller owed 4M but treasury is empty, gets 0. Controller ends with 2M (5M - 3M).
            
            string dbName = Guid.NewGuid().ToString();
            var context = GetDbContext(dbName);

            var controllerId = Guid.NewGuid();
            var playerAId = Guid.NewGuid();
            var playerBId = Guid.NewGuid();

            var controller = new Player { Id = controllerId, UserId = "user-c", Cash = 5 };
            var playerA = new Player { Id = playerAId, UserId = "user-a", Cash = 0 };
            var playerB = new Player { Id = playerBId, UserId = "user-b", Cash = 0 };

            var gameId = Guid.NewGuid();
            var game = new Game
            {
                Id = gameId,
                Name = "Test Game",
                Status = GameStatus.InProgress,
                CurrentTurnNation = Nation.Europe,
                Players = new List<Player> { controller, playerA, playerB },
                NationStates = new List<NationState>
                {
                    new NationState { Nation = Nation.Europe, ControllerId = controllerId, Treasury = 4, RondelPosition = 3, Power = 0 },
                },
                Bonds = new List<Bond>
                {
                    new Bond { Id = Guid.NewGuid(), Nation = Nation.Europe, Cost = 4, Interest = 2, HolderId = playerBId },
                    new Bond { Id = Guid.NewGuid(), Nation = Nation.Europe, Cost = 12, Interest = 5, HolderId = playerAId },
                    new Bond { Id = Guid.NewGuid(), Nation = Nation.Europe, Cost = 9, Interest = 4, HolderId = controllerId }
                }
            };
            context.Games.Add(game);
            await context.SaveChangesAsync();

            GamesController.HandleInvestorPhase(context, game, game.NationStates.First(), controller, isLandedOn: true);

            var dbPlayerA = await context.Players.FindAsync(playerAId);
            var dbPlayerB = await context.Players.FindAsync(playerBId);
            var dbController = await context.Players.FindAsync(controllerId);
            var dbNation = game.NationStates.First();

            Assert.Equal(2, dbPlayerB.Cash); // Full 2M
            Assert.Equal(5, dbPlayerA.Cash); // Full 5M
            Assert.Equal(2, dbController.Cash); // 5M - 3M
            Assert.Equal(0, dbNation.Treasury);
        }

        [Fact]
        public async Task TestControllerContributesPartialCash()
        {
            // Case 3: Treasury insufficient. Controller has some cash, but not enough to cover full deficit.
            // B has 2M interest. A has 5M interest. Owed to others = 7M.
            // Treasury is 2M. Deficit = 5M.
            // Controller has 3M cash.
            // Total for others = 2 + 3 = 5M.
            // Expected: B gets 2M. A gets 3M (partial).
            // Controller ends with 0M.
            
            string dbName = Guid.NewGuid().ToString();
            var context = GetDbContext(dbName);

            var controllerId = Guid.NewGuid();
            var playerAId = Guid.NewGuid();
            var playerBId = Guid.NewGuid();

            var controller = new Player { Id = controllerId, UserId = "user-c", Cash = 3 };
            var playerA = new Player { Id = playerAId, UserId = "user-a", Cash = 0 };
            var playerB = new Player { Id = playerBId, UserId = "user-b", Cash = 0 };

            var gameId = Guid.NewGuid();
            var game = new Game
            {
                Id = gameId,
                Name = "Test Game",
                Status = GameStatus.InProgress,
                CurrentTurnNation = Nation.Europe,
                Players = new List<Player> { controller, playerA, playerB },
                NationStates = new List<NationState>
                {
                    new NationState { Nation = Nation.Europe, ControllerId = controllerId, Treasury = 2, RondelPosition = 3, Power = 0 },
                },
                Bonds = new List<Bond>
                {
                    new Bond { Id = Guid.NewGuid(), Nation = Nation.Europe, Cost = 4, Interest = 2, HolderId = playerBId },
                    new Bond { Id = Guid.NewGuid(), Nation = Nation.Europe, Cost = 12, Interest = 5, HolderId = playerAId },
                    new Bond { Id = Guid.NewGuid(), Nation = Nation.Europe, Cost = 9, Interest = 4, HolderId = controllerId }
                }
            };
            context.Games.Add(game);
            await context.SaveChangesAsync();

            GamesController.HandleInvestorPhase(context, game, game.NationStates.First(), controller, isLandedOn: true);

            var dbPlayerA = await context.Players.FindAsync(playerAId);
            var dbPlayerB = await context.Players.FindAsync(playerBId);
            var dbController = await context.Players.FindAsync(controllerId);
            var dbNation = game.NationStates.First();

            Assert.Equal(2, dbPlayerB.Cash); // Full 2M
            Assert.Equal(3, dbPlayerA.Cash); // Partial 3M
            Assert.Equal(0, dbController.Cash); // Spent all cash
            Assert.Equal(0, dbNation.Treasury);
        }
        [Fact]
        public async Task TestTreasuryCoversOthers_PartiallyCoversController()
        {
            // Case 4: Treasury is enough for others, but only partially covers the controller.
            // B has 2M. A has 0M. Controller has 4M. Owed to others = 2M. Owed to controller = 4M.
            // Treasury is 5M.
            // Expected: B gets 2M. Controller gets 3M (partial). Treasury = 0.
            
            string dbName = Guid.NewGuid().ToString();
            var context = GetDbContext(dbName);

            var controllerId = Guid.NewGuid();
            var playerBId = Guid.NewGuid();

            var controller = new Player { Id = controllerId, UserId = "user-c", Cash = 0 };
            var playerB = new Player { Id = playerBId, UserId = "user-b", Cash = 0 };

            var gameId = Guid.NewGuid();
            var game = new Game
            {
                Id = gameId,
                Name = "Test Game",
                Status = GameStatus.InProgress,
                CurrentTurnNation = Nation.Europe,
                Players = new List<Player> { controller, playerB },
                NationStates = new List<NationState>
                {
                    new NationState { Nation = Nation.Europe, ControllerId = controllerId, Treasury = 5, RondelPosition = 3, Power = 0 },
                },
                Bonds = new List<Bond>
                {
                    new Bond { Id = Guid.NewGuid(), Nation = Nation.Europe, Cost = 4, Interest = 2, HolderId = playerBId },
                    new Bond { Id = Guid.NewGuid(), Nation = Nation.Europe, Cost = 9, Interest = 4, HolderId = controllerId }
                }
            };
            context.Games.Add(game);
            await context.SaveChangesAsync();

            GamesController.HandleInvestorPhase(context, game, game.NationStates.First(), controller, isLandedOn: true);

            var dbPlayerB = await context.Players.FindAsync(playerBId);
            var dbController = await context.Players.FindAsync(controllerId);
            var dbNation = game.NationStates.First();

            Assert.Equal(2, dbPlayerB.Cash);
            Assert.Equal(3, dbController.Cash);
            Assert.Equal(0, dbNation.Treasury);
        }

        [Fact]
        public async Task TestTreasuryCoversEveryone()
        {
            // Case 5: Treasury is enough for everyone.
            // B has 2M. Controller has 4M. Treasury is 10M.
            // Expected: B gets 2M. Controller gets 4M. Treasury = 4M.
            
            string dbName = Guid.NewGuid().ToString();
            var context = GetDbContext(dbName);

            var controllerId = Guid.NewGuid();
            var playerBId = Guid.NewGuid();

            var controller = new Player { Id = controllerId, UserId = "user-c", Cash = 0 };
            var playerB = new Player { Id = playerBId, UserId = "user-b", Cash = 0 };

            var gameId = Guid.NewGuid();
            var game = new Game
            {
                Id = gameId,
                Name = "Test Game",
                Status = GameStatus.InProgress,
                CurrentTurnNation = Nation.Europe,
                Players = new List<Player> { controller, playerB },
                NationStates = new List<NationState>
                {
                    new NationState { Nation = Nation.Europe, ControllerId = controllerId, Treasury = 10, RondelPosition = 3, Power = 0 },
                },
                Bonds = new List<Bond>
                {
                    new Bond { Id = Guid.NewGuid(), Nation = Nation.Europe, Cost = 4, Interest = 2, HolderId = playerBId },
                    new Bond { Id = Guid.NewGuid(), Nation = Nation.Europe, Cost = 9, Interest = 4, HolderId = controllerId }
                }
            };
            context.Games.Add(game);
            await context.SaveChangesAsync();

            GamesController.HandleInvestorPhase(context, game, game.NationStates.First(), controller, isLandedOn: true);

            var dbPlayerB = await context.Players.FindAsync(playerBId);
            var dbController = await context.Players.FindAsync(controllerId);
            var dbNation = game.NationStates.First();

            Assert.Equal(2, dbPlayerB.Cash);
            Assert.Equal(4, dbController.Cash);
            Assert.Equal(4, dbNation.Treasury);
        }

        [Fact]
        public async Task TestControllerIsOnlyBondHolder_TreasuryEmpty()
        {
            // Case 6: Controller is the only bond holder, but treasury is empty.
            // Controller shouldn't pay themselves out of pocket.
            // Controller has 4M interest. Treasury is 0M. Controller cash is 10M.
            // Expected: Controller keeps 10M cash. Treasury = 0.
            
            string dbName = Guid.NewGuid().ToString();
            var context = GetDbContext(dbName);

            var controllerId = Guid.NewGuid();

            var controller = new Player { Id = controllerId, UserId = "user-c", Cash = 10 };

            var gameId = Guid.NewGuid();
            var game = new Game
            {
                Id = gameId,
                Name = "Test Game",
                Status = GameStatus.InProgress,
                CurrentTurnNation = Nation.Europe,
                Players = new List<Player> { controller },
                NationStates = new List<NationState>
                {
                    new NationState { Nation = Nation.Europe, ControllerId = controllerId, Treasury = 0, RondelPosition = 3, Power = 0 },
                },
                Bonds = new List<Bond>
                {
                    new Bond { Id = Guid.NewGuid(), Nation = Nation.Europe, Cost = 9, Interest = 4, HolderId = controllerId }
                }
            };
            context.Games.Add(game);
            await context.SaveChangesAsync();

            GamesController.HandleInvestorPhase(context, game, game.NationStates.First(), controller, isLandedOn: true);

            var dbController = await context.Players.FindAsync(controllerId);
            var dbNation = game.NationStates.First();

            Assert.Equal(10, dbController.Cash);
            Assert.Equal(0, dbNation.Treasury);
        }
    }
}
