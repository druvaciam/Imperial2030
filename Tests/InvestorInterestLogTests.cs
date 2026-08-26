using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Imperial2030.Server.Controllers;
using Imperial2030.Server.Data;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Imperial2030.Tests
{
    /// <summary>
    /// How the Investor turn's interest payout is written to the game log.
    ///
    /// Two defects showed up in one live log line, from a Russian Investor turn:
    ///
    ///   Bot Alpha (RL-3) Russia paid 3M interest to player1
    ///   Bot Alpha (RL-3) Russia paid 2M interest to player1
    ///
    ///   1. player1 got two lines because the payout logged once per BOND, while the controller's own
    ///      payment was logged once for the summed total. It looked like humans were treated differently
    ///      from bots; in fact it was whoever happened to hold more than one bond in that nation.
    ///   2. The payment is attributed to the controller, but interest comes out of the NATIONAL TREASURY
    ///      (Imperial-2030-Rules.pdf p.11: "each player who has granted bonds to the nation gets paid
    ///      interest by the national treasury"). The controller is not the payer here — that only happens
    ///      in the separate personal-contribution branch.
    ///
    /// These entries are safe to reshape: "Investor" is on GameReplayService's skip-list, so replay
    /// applies no state from them, and TcpTrainingServer's reward scan reads only the PersonalContribution
    /// and MissedInterest metadata, which other log helpers produce.
    /// </summary>
    public class InvestorInterestLogTests
    {
        private static ApplicationDbContext GetDbContext() =>
            new(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

        private static List<(int PaidAmount, string Payee, string LoggedBy)> InterestPaidEntries(Game game) =>
            game.Actions
                .Where(a => a.ActionType == "Investor" && !string.IsNullOrEmpty(a.Metadata))
                .Select(a => (Action: a, Meta: JsonSerializer.Deserialize<InvestorMetadata>(
                    a.Metadata!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })))
                .Where(x => x.Meta?.Type == "InterestPaid")
                .Select(x => (x.Meta!.PaidAmount ?? 0, x.Meta.PayeeName ?? "", x.Action.PlayerName))
                .ToList();

        /// <summary>
        /// Treasury covers everything, and one non-controller holds two bonds. That holder must get a
        /// single combined line, exactly as the controller already did for its own bonds.
        /// </summary>
        [Fact]
        public async Task AHolderWithTwoBondsGetsOneCombinedInterestLine()
        {
            using var context = GetDbContext();

            var controllerId = Guid.NewGuid();
            var twoBondHolderId = Guid.NewGuid();
            var oneBondHolderId = Guid.NewGuid();

            var controller = new Player { Id = controllerId, UserId = "user-c", Cash = 0 };
            var game = new Game
            {
                Id = Guid.NewGuid(),
                Name = "Interest Log",
                Status = GameStatus.InProgress,
                CurrentTurnNation = Nation.Russia,
                Players = new List<Player>
                {
                    controller,
                    new Player { Id = twoBondHolderId, UserId = "user-two", BotName = "player1", Cash = 0 },
                    new Player { Id = oneBondHolderId, UserId = "user-one", BotName = "Bot Echo", Cash = 0 }
                },
                NationStates = new List<NationState>
                {
                    new NationState { Nation = Nation.Russia, ControllerId = controllerId, Treasury = 50 }
                },
                Bonds = new List<Bond>
                {
                    new Bond { Id = Guid.NewGuid(), Nation = Nation.Russia, Cost = 9,  Interest = 3, HolderId = twoBondHolderId },
                    new Bond { Id = Guid.NewGuid(), Nation = Nation.Russia, Cost = 6,  Interest = 2, HolderId = twoBondHolderId },
                    new Bond { Id = Guid.NewGuid(), Nation = Nation.Russia, Cost = 4,  Interest = 1, HolderId = oneBondHolderId },
                    new Bond { Id = Guid.NewGuid(), Nation = Nation.Russia, Cost = 30, Interest = 9, HolderId = controllerId }
                }
            };
            context.Games.Add(game);
            await context.SaveChangesAsync();

            GamesController.HandleInvestorPhase(context, game, game.NationStates.First(), controller, isLandedOn: true);

            var entries = InterestPaidEntries(game);

            // One line per payee, not per bond.
            Assert.Equal(3, entries.Count);
            var combined = Assert.Single(entries, e => e.Payee == "player1");
            Assert.Equal(5, combined.PaidAmount); // 3 + 2

            // Cash is unchanged by the grouping - the point is purely how it is reported.
            Assert.Equal(5, context.Players.Local.First(p => p.Id == twoBondHolderId).Cash);
            Assert.Equal(1, context.Players.Local.First(p => p.Id == oneBondHolderId).Cash);
            Assert.Equal(9, controller.Cash);
        }

        /// <summary>
        /// Interest is paid by the treasury, so no player is the actor. Logging the controller's name
        /// read as though they had paid it personally, which is a different (and separately logged) thing.
        /// </summary>
        [Fact]
        public async Task InterestPaymentIsAttributedToTheNationNotTheController()
        {
            using var context = GetDbContext();

            var controllerId = Guid.NewGuid();
            var holderId = Guid.NewGuid();
            var controller = new Player { Id = controllerId, UserId = "user-c", BotName = "Bot Alpha (RL-3)", Cash = 0 };

            var game = new Game
            {
                Id = Guid.NewGuid(),
                Name = "Interest Log",
                Status = GameStatus.InProgress,
                CurrentTurnNation = Nation.Russia,
                Players = new List<Player>
                {
                    controller,
                    new Player { Id = holderId, UserId = "user-h", BotName = "player1", Cash = 0 }
                },
                NationStates = new List<NationState>
                {
                    new NationState { Nation = Nation.Russia, ControllerId = controllerId, Treasury = 50 }
                },
                Bonds = new List<Bond>
                {
                    new Bond { Id = Guid.NewGuid(), Nation = Nation.Russia, Cost = 6, Interest = 2, HolderId = holderId },
                    new Bond { Id = Guid.NewGuid(), Nation = Nation.Russia, Cost = 9, Interest = 3, HolderId = controllerId }
                }
            };
            context.Games.Add(game);
            await context.SaveChangesAsync();

            GamesController.HandleInvestorPhase(context, game, game.NationStates.First(), controller, isLandedOn: true);

            var entries = InterestPaidEntries(game);
            Assert.NotEmpty(entries);
            Assert.All(entries, e => Assert.Equal(GameConstants.SystemPlayerName, e.LoggedBy));
            Assert.DoesNotContain(entries, e => e.LoggedBy == "Bot Alpha (RL-3)");

            // The nation is still recorded, so the line can read "Russia paid ... to ...".
            var interestActions = game.Actions.Where(a => a.ActionType == "Investor").ToList();
            Assert.All(interestActions, a => Assert.Equal(Nation.Russia, a.Nation));
        }

        /// <summary>
        /// The controller personally covering a shortfall is a genuine player action and must keep being
        /// attributed to them — TcpTrainingServer's reward scan matches those entries by PlayerName, so
        /// re-attributing them to the nation would silently disable the personal-contribution penalty.
        /// </summary>
        [Fact]
        public async Task PersonalContributionIsStillAttributedToTheController()
        {
            using var context = GetDbContext();

            var controllerId = Guid.NewGuid();
            var holderId = Guid.NewGuid();
            var controller = new Player { Id = controllerId, UserId = "user-c", BotName = "Bot Alpha (RL-3)", Cash = 10 };

            var game = new Game
            {
                Id = Guid.NewGuid(),
                Name = "Interest Log",
                Status = GameStatus.InProgress,
                CurrentTurnNation = Nation.Russia,
                Players = new List<Player>
                {
                    controller,
                    new Player { Id = holderId, UserId = "user-h", BotName = "player1", Cash = 0 }
                },
                NationStates = new List<NationState>
                {
                    // Treasury cannot cover the 6M owed to the other holder, so the controller pays.
                    new NationState { Nation = Nation.Russia, ControllerId = controllerId, Treasury = 1 }
                },
                Bonds = new List<Bond>
                {
                    new Bond { Id = Guid.NewGuid(), Nation = Nation.Russia, Cost = 20, Interest = 6, HolderId = holderId },
                    new Bond { Id = Guid.NewGuid(), Nation = Nation.Russia, Cost = 9,  Interest = 3, HolderId = controllerId }
                }
            };
            context.Games.Add(game);
            await context.SaveChangesAsync();

            GamesController.HandleInvestorPhase(context, game, game.NationStates.First(), controller, isLandedOn: true);

            var contribution = game.Actions
                .Where(a => a.ActionType == "Investor" && !string.IsNullOrEmpty(a.Metadata))
                .Select(a => (a.PlayerName, Meta: JsonSerializer.Deserialize<InvestorMetadata>(
                    a.Metadata!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })))
                .Where(x => x.Meta?.PersonalContribution > 0)
                .ToList();

            Assert.NotEmpty(contribution);
            Assert.All(contribution, c => Assert.Equal("Bot Alpha (RL-3)", c.PlayerName));
        }
    }
}
