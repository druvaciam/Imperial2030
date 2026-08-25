using System;
using System.Collections.Generic;
using System.Linq;
using Imperial2030.Server.Helpers;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Models;
using Xunit;

namespace Imperial2030.Tests
{
    /// <summary>
    /// Join codes gate entry to private games, so they are a security boundary and must come from a
    /// cryptographic source rather than <c>new Random()</c>.
    /// </summary>
    public class JoinCodeTests
    {
        [Fact]
        public void Generate_ProducesSixUppercaseAlphanumerics()
        {
            for (int i = 0; i < 50; i++)
            {
                var code = JoinCodeGenerator.Generate();

                Assert.Equal(6, code.Length);
                Assert.All(code, c => Assert.Contains(c, "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"));
            }
        }

        [Fact]
        public void Generate_DoesNotRepeatItselfAcrossRapidSuccessiveCalls()
        {
            // The failure mode being guarded against: a time-seeded generator constructed per call can
            // hand out the same code twice when two games are created in the same instant.
            var codes = new HashSet<string>();
            for (int i = 0; i < 500; i++) codes.Add(JoinCodeGenerator.Generate());

            Assert.Equal(500, codes.Count);
        }

        [Fact]
        public void Generate_UsesTheWholeAlphabet()
        {
            // A weak source that only ever reaches part of the alphabet would shrink the keyspace well
            // below 36^6 without changing the code's shape.
            var seen = new HashSet<char>();
            for (int i = 0; i < 2000; i++)
            {
                foreach (var c in JoinCodeGenerator.Generate()) seen.Add(c);
            }

            Assert.Equal(36, seen.Count);
        }
    }

    /// <summary>
    /// Final ranking decides <c>WinnerName</c>, which import/replay must reproduce exactly
    /// (<c>TestImportFromExportedJson</c> asserts it). The rulebook's tie-break chain (p.6) can still end
    /// in an absolute tie, and the comparison then returns 0 — so the sort must at least be deterministic
    /// for identical input.
    /// </summary>
    public class RankedPlayersDeterminismTests
    {
        private static Game BuildAbsolutelyTiedGame(int playerCount)
        {
            var game = new Game
            {
                Players = new List<Player>(),
                NationStates = new List<NationState> { new NationState { Nation = Nation.Russia, Power = 10 } },
                Bonds = new List<Bond>()
            };

            // Identical cash, identical bonds: every step of the p.6 chain compares equal, so the
            // comparison falls through to "absolute tie".
            for (int i = 0; i < playerCount; i++)
            {
                var player = new Player { Id = Guid.NewGuid(), Cash = 10 };
                game.Players.Add(player);
                game.Bonds.Add(new Bond { Nation = Nation.Russia, Cost = 6, Interest = 3, HolderId = player.Id });
            }

            return game;
        }

        [Fact]
        public void GetRankedPlayers_WithAnAbsoluteTie_IsStableAcrossRepeatedCalls()
        {
            // List.Sort is an introsort: unstable, and for more than a handful of equal elements it will
            // reorder them. Same game, same call, must mean the same winner.
            var game = BuildAbsolutelyTiedGame(playerCount: 20);
            var expected = game.GetRankedPlayers().Select(p => p.Id).ToList();

            for (int i = 0; i < 20; i++)
            {
                Assert.Equal(expected, game.GetRankedPlayers().Select(p => p.Id).ToList());
            }
        }

        [Fact]
        public void GetRankedPlayers_WithAnAbsoluteTie_PreservesTheRosterOrder()
        {
            // The rulebook does not settle an absolute tie, so no winner is invented here: the roster
            // order is simply carried through rather than scrambled.
            var game = BuildAbsolutelyTiedGame(playerCount: 20);

            var ranked = game.GetRankedPlayers().Select(p => p.Id).ToList();

            Assert.Equal(game.Players.Select(p => p.Id).ToList(), ranked);
        }

        [Fact]
        public void GetRankedPlayers_StillAppliesTheRulebookTieBreak()
        {
            // Guards that stability did not come at the cost of the p.6 chain: the player with the higher
            // credit sum in the most powerful nation must win regardless of roster position.
            var game = new Game
            {
                NationStates = new List<NationState> { new NationState { Nation = Nation.Russia, Power = 15 } },
                Players = new List<Player>(),
                Bonds = new List<Bond>()
            };

            var weaker = new Player { Id = Guid.NewGuid(), Cash = 13 };
            var stronger = new Player { Id = Guid.NewGuid(), Cash = 10 };
            game.Players.Add(weaker);   // listed FIRST, so roster order alone would rank them first
            game.Players.Add(stronger);

            // Both score 25: weaker = 13 cash + 4 interest x 3, stronger = 10 cash + 5 interest x 3.
            game.Bonds.Add(new Bond { Nation = Nation.Russia, Cost = 9, Interest = 4, HolderId = weaker.Id });
            game.Bonds.Add(new Bond { Nation = Nation.Russia, Cost = 12, Interest = 5, HolderId = stronger.Id });

            var ranked = game.GetRankedPlayers();

            Assert.Equal(stronger.Id, ranked[0].Id);
        }
    }
}
