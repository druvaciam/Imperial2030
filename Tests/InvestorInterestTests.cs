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

        [Fact]
        public void HandleInvestorPhase_InvestorCardHolderInvestsBeforeSwissBankPlayers()
        {
            // Imperial-2030-Rules.pdf p.11 numbers the Investor turn's steps: "2. Activating the Investor -
            // The player who is holding the Investor card (Investor) gets paid 2 million from the bank and
            // then may invest in any nation." followed by "3. Investing as Swiss Bank - Each player who has
            // a Swiss Bank and does not hold the investor card at the same time is also allowed to invest
            // once."
            //
            // So the card holder picks first and the Swiss Banks pick after. Order is not cosmetic: bonds
            // are a scarce shared pool and the trade-in mechanic makes first pick materially valuable, so
            // whoever the queue puts first can take a bond the other one wanted.
            //
            // The queue is exactly the investment order - ActingPlayerId acts now and PendingInvestorIds is
            // drained one at a time (GamesController.cs:1733-1736).
            var investorId = Guid.NewGuid();
            var swissBankId = Guid.NewGuid();
            var rivalId = Guid.NewGuid();

            var investor = new Player { Id = investorId, BotName = "Investor" };
            var swissBanker = new Player { Id = swissBankId, BotName = "SwissBanker" };
            var rival = new Player { Id = rivalId, BotName = "Rival" };

            var game = new Game
            {
                Id = Guid.NewGuid(),
                InvestorCardHolderId = investorId,
                Players = new List<Player> { investor, swissBanker, rival },
                NationStates = new List<NationState>
                {
                    // swissBanker controls no nation, which is what makes them a Swiss Bank (p.12).
                    new NationState { Nation = Nation.Russia, ControllerId = investorId },
                    new NationState { Nation = Nation.China, ControllerId = rivalId }
                },
                Bonds = new List<Bond>()
            };

            var russia = game.NationStates.First(n => n.Nation == Nation.Russia);

            // isLandedOn: false - the Investor space was only passed over, which per p.11 still runs steps
            // two and three. Keeps this test on the ordering and out of the interest-payout branch.
            GamesController.HandleInvestorPhase(null, game, russia, investor, isLandedOn: false);

            Assert.Equal(investorId, game.ActingPlayerId);
            Assert.Equal(new List<Guid> { swissBankId }, game.PendingInvestorIds);
        }

        [Fact]
        public void HandleInvestorPhase_CardHolderWhoAlsoHasASwissBank_InvestsOnlyOnce()
        {
            // Imperial-2030-Rules.pdf FAQ p.14: "Can the investor invest twice if he owns a Swiss Bank?
            // No." The card holder here controls no nation, so they qualify as a Swiss Bank too and are
            // picked up by both halves of the queue construction - they must still appear exactly once.
            var investorId = Guid.NewGuid();
            var rivalId = Guid.NewGuid();

            var investor = new Player { Id = investorId, BotName = "Investor" };
            var rival = new Player { Id = rivalId, BotName = "Rival" };

            var game = new Game
            {
                Id = Guid.NewGuid(),
                InvestorCardHolderId = investorId,
                Players = new List<Player> { investor, rival },
                NationStates = new List<NationState>
                {
                    // The card holder controls nothing - they hold a Swiss Bank as well as the card.
                    new NationState { Nation = Nation.Russia, ControllerId = rivalId }
                },
                Bonds = new List<Bond>()
            };

            var russia = game.NationStates.First(n => n.Nation == Nation.Russia);

            GamesController.HandleInvestorPhase(null, game, russia, rival, isLandedOn: false);

            Assert.Equal(investorId, game.ActingPlayerId);
            Assert.Empty(game.PendingInvestorIds);
        }

        [Fact]
        public void HandleInvestorPhase_SeveralSwissBanks_AreOrderedFromTheInvestorCardHolder()
        {
            // Imperial-2030-Rules.pdf p.11: "If several players have a Swiss Bank, investing is done in the
            // order of play (clockwise), starting from the player currently with the Investor card."
            //
            // Play order here is [first, second, third, fourth]. The card holder is `third`, so the order
            // of play counting from them is third -> fourth -> first -> second. `fourth` and `first` hold
            // Swiss Banks (they control no nation), so they invest in THAT order - fourth, then first -
            // not in the unrotated [first, fourth] the plain player ordering would give.
            //
            // "Starting from the player with the Investor card" is unambiguous here even though the card
            // holder is themselves in the rotation: they are already queued first as the Investor, so the
            // Swiss Bank pass skips them either way (FAQ p.14).
            var ordered = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() }.OrderBy(g => g).ToList();
            var first = ordered[0];
            var second = ordered[1];
            var third = ordered[2];
            var fourth = ordered[3];

            var game = new Game
            {
                Id = Guid.NewGuid(),
                InvestorCardHolderId = third,
                Players = new List<Player>
                {
                    // Deliberately not in play order - the code must derive the order, not take this list's.
                    new Player { Id = fourth, BotName = "Fourth" },
                    new Player { Id = first, BotName = "First" },
                    new Player { Id = third, BotName = "Third" },
                    new Player { Id = second, BotName = "Second" }
                },
                NationStates = new List<NationState>
                {
                    new NationState { Nation = Nation.Russia, ControllerId = third },
                    new NationState { Nation = Nation.China, ControllerId = second }
                    // `first` and `fourth` control nothing: two simultaneous Swiss Banks.
                },
                Bonds = new List<Bond>()
            };

            var russia = game.NationStates.First(n => n.Nation == Nation.Russia);
            var cardHolder = game.Players.First(p => p.Id == third);

            GamesController.HandleInvestorPhase(null, game, russia, cardHolder, isLandedOn: false);

            Assert.Equal(third, game.ActingPlayerId);
            Assert.Equal(new List<Guid> { fourth, first }, game.PendingInvestorIds);
        }
    }
}
