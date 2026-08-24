using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Imperial2030.Server.Data;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Imperial2030.Tests
{
    /// <summary>
    /// Guards the `.AsSplitQuery()` fix in GameReplayService against a REAL relational provider.
    ///
    /// Every other replay test in this suite runs on the EF InMemory provider, where AsSplitQuery is a
    /// no-op — it has no SQL to split. That means the InMemory suite passing says nothing about whether
    /// these query shapes are correct once split, which is the only situation where the change does
    /// anything at all. A split query fans out into one SELECT per collection; if the child queries did
    /// not carry the parent's filter correctly, the navigations would come back empty or wrong on a real
    /// deployment while every InMemory test stayed green.
    ///
    /// SQLite is used because it is a real relational provider that needs no server, and it is one of the
    /// three providers this app actually ships with (Program.cs).
    /// </summary>
    public class SplitQueryRelationalTests : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly List<string> _sqlLog = new();

        public SplitQueryRelationalTests()
        {
            // A SQLite in-memory database lives only as long as its connection, so it is held open for
            // the lifetime of the fixture rather than per-context.
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
        }

        public void Dispose() => _connection.Dispose();

        private ApplicationDbContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .LogTo(line => { lock (_sqlLog) _sqlLog.Add(line); }, LogLevel.Information)
                .Options;
            return new ApplicationDbContext(options);
        }

        private async Task<Guid> SeedGameAsync()
        {
            using var context = CreateContext();
            await context.Database.EnsureCreatedAsync();

            var gameId = Guid.NewGuid();
            var game = new Game { Id = gameId, Name = "Split Query Game", Status = GameStatus.InProgress };
            context.Games.Add(game);

            // Two collections, different sizes, so a cartesian product or a mis-filtered child query
            // produces a count that differs from the truth in an obvious way.
            var players = Enumerable.Range(0, 4)
                .Select(i => new Player { Id = Guid.NewGuid(), GameId = gameId, BotName = $"Bot {i}", IsBot = true })
                .ToList();
            context.Players.AddRange(players);

            foreach (var nation in Enum.GetValues<Nation>())
            {
                context.NationStates.Add(new NationState
                {
                    GameId = gameId,
                    Nation = nation,
                    ControllerId = players[0].Id,
                    Treasury = (int)nation
                });
            }

            // A second game whose rows must never leak into the first game's navigations.
            var otherId = Guid.NewGuid();
            context.Games.Add(new Game { Id = otherId, Name = "Other", Status = GameStatus.InProgress });
            context.Players.Add(new Player { Id = Guid.NewGuid(), GameId = otherId, BotName = "Intruder", IsBot = true });
            context.NationStates.Add(new NationState { GameId = otherId, Nation = Nation.Russia, Treasury = 999 });

            await context.SaveChangesAsync();
            return gameId;
        }

        /// <summary>
        /// The exact shape GameReplayService uses on its hottest path: two collection Includes, split,
        /// terminated by FirstAsync on the primary key.
        /// </summary>
        [Fact]
        public async Task SplitQuery_WithTwoCollectionIncludes_LoadsBothNavigationsCorrectly()
        {
            var gameId = await SeedGameAsync();
            using var context = CreateContext();

            var game = await context.Games
                .Include(g => g.Players)
                .Include(g => g.NationStates)
                .AsSplitQuery()
                .FirstAsync(g => g.Id == gameId);

            Assert.Equal(4, game.Players.Count);
            Assert.Equal(Enum.GetValues<Nation>().Length, game.NationStates.Count);

            // No rows from the other game leaked in, and no row was duplicated by a join.
            Assert.All(game.Players, p => Assert.Equal(gameId, p.GameId));
            Assert.All(game.NationStates, ns => Assert.Equal(gameId, ns.GameId));
            Assert.DoesNotContain(game.Players, p => p.BotName == "Intruder");
            Assert.Equal(game.Players.Count, game.Players.Select(p => p.Id).Distinct().Count());
        }

        /// <summary>
        /// Confirms the query really is being split rather than silently falling back to a single join —
        /// otherwise this whole fix would be a no-op on the provider it was written for.
        /// </summary>
        [Fact]
        public async Task SplitQuery_ActuallyIssuesSeparateSelectStatements()
        {
            var gameId = await SeedGameAsync();

            lock (_sqlLog) _sqlLog.Clear();

            using (var context = CreateContext())
            {
                await context.Games
                    .Include(g => g.Players)
                    .Include(g => g.NationStates)
                    .AsSplitQuery()
                    .FirstAsync(g => g.Id == gameId);
            }

            int selectCount;
            lock (_sqlLog)
            {
                selectCount = _sqlLog.Count(line => line.Contains("SELECT", StringComparison.Ordinal));
            }

            // One SELECT for the root plus one per included collection.
            Assert.True(selectCount >= 3,
                $"Expected the query to be split into at least 3 SELECT statements, saw {selectCount}.");
        }

        /// <summary>
        /// The symptom rule #19 exists to prevent: without splitting, EF joins both collections into one
        /// statement and returns players x nation-states rows. EF still de-duplicates by identity, so the
        /// object graph is correct either way — the cost is in the rows crossing the wire, which is why
        /// this is a scaling bug rather than a wrong-answer bug, and why it needs a mechanical scan
        /// (tools/scan_splitquery.py) rather than a passing test to catch.
        /// </summary>
        [Fact]
        public async Task SingleQuery_ProducesOneStatement_DocumentingWhatTheFixAvoids()
        {
            var gameId = await SeedGameAsync();

            lock (_sqlLog) _sqlLog.Clear();

            using (var context = CreateContext())
            {
                await context.Games
                    .Include(g => g.Players)
                    .Include(g => g.NationStates)
                    .AsSingleQuery()
                    .FirstAsync(g => g.Id == gameId);
            }

            int selectCount;
            lock (_sqlLog)
            {
                selectCount = _sqlLog.Count(line => line.Contains("SELECT", StringComparison.Ordinal));
            }

            Assert.True(selectCount < 3,
                $"Single-query mode should emit one joined statement, saw {selectCount} SELECTs.");
        }
    }
}
