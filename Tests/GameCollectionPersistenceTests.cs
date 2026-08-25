using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Imperial2030.Server.Data;
using Imperial2030.Server.Models;
using Imperial2030.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Imperial2030.Tests
{
    /// <summary>
    /// `Game` carries three list-shaped bits of pending state, mapped two different ways:
    ///
    ///   * `PendingBattleDefenders` and `PendingSwissBankResponders` are plain `List&lt;T&gt;` properties,
    ///     which EF maps automatically as primitive collections (`PrimitiveCollection&lt;string&gt;` in the
    ///     model snapshot) — real backing lists, so in-place mutation works.
    ///   * `PendingInvestorIds` used to be `[NotMapped]`, backed by a hand-serialised
    ///     `PendingInvestorIdsJson` column whose getter deserialised a FRESH list on every read — so
    ///     in-place mutation was a silent no-op while the identical call on its two neighbours worked.
    ///     It is now a primitive collection like the other two.
    ///
    /// These run against real SQLite because the behaviour lives in the provider's change tracking, and
    /// the InMemory provider the rest of the suite uses cannot show it.
    /// </summary>
    public class GameCollectionPersistenceTests : IDisposable
    {
        private readonly SqliteConnection _connection;

        public GameCollectionPersistenceTests()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
        }

        public void Dispose() => _connection.Dispose();

        private ApplicationDbContext CreateContext() =>
            new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection).Options);

        private async Task<Guid> SeedAsync()
        {
            using var context = CreateContext();
            await context.Database.EnsureCreatedAsync();

            var gameId = Guid.NewGuid();
            context.Games.Add(new Game
            {
                Id = gameId,
                Name = "Collection Persistence",
                Status = GameStatus.InProgress,
                PendingBattleDefenders = new List<Nation> { Nation.Russia, Nation.Europe },
                PendingSwissBankResponders = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() },
                PendingInvestorIds = new List<Guid> { Guid.NewGuid() }
            });
            await context.SaveChangesAsync();
            return gameId;
        }

        [Fact]
        public async Task PrimitiveCollections_RoundTrip()
        {
            var gameId = await SeedAsync();

            using var read = CreateContext();
            var game = await read.Games.FirstAsync(g => g.Id == gameId);

            Assert.Equal(new[] { Nation.Russia, Nation.Europe }, game.PendingBattleDefenders);
            Assert.Equal(2, game.PendingSwissBankResponders.Count);
            Assert.Single(game.PendingInvestorIds);
        }

        [Fact]
        public async Task ClearingAPrimitiveCollectionInPlace_Persists()
        {
            // ManeuverController.BattleResponse and BotService both do exactly this - `.Clear()` on the
            // tracked entity, with no reassignment - to end a pending battle. If EF did not notice the
            // in-place edit, the battle would still be pending after the save and the game would deadlock
            // waiting for responses that had already been given.
            var gameId = await SeedAsync();

            using (var mutate = CreateContext())
            {
                var game = await mutate.Games.FirstAsync(g => g.Id == gameId);
                game.PendingBattleDefenders.Clear();
                game.PendingSwissBankResponders.Clear();
                await mutate.SaveChangesAsync();
            }

            using var read = CreateContext();
            var reloaded = await read.Games.FirstAsync(g => g.Id == gameId);

            Assert.Empty(reloaded.PendingBattleDefenders);
            Assert.Empty(reloaded.PendingSwissBankResponders);
        }

        [Fact]
        public async Task AddingToAPrimitiveCollectionInPlace_Persists()
        {
            var gameId = await SeedAsync();

            using (var mutate = CreateContext())
            {
                var game = await mutate.Games.FirstAsync(g => g.Id == gameId);
                game.PendingBattleDefenders.Add(Nation.China);
                await mutate.SaveChangesAsync();
            }

            using var read = CreateContext();
            var reloaded = await read.Games.FirstAsync(g => g.Id == gameId);

            Assert.Contains(Nation.China, reloaded.PendingBattleDefenders);
        }

        [Fact]
        public async Task ReassigningPendingInvestorIds_Persists()
        {
            // The pattern every call site uses, and the only one that works for this property.
            var gameId = await SeedAsync();
            var replacement = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };

            using (var mutate = CreateContext())
            {
                var game = await mutate.Games.FirstAsync(g => g.Id == gameId);
                game.PendingInvestorIds = replacement;
                await mutate.SaveChangesAsync();
            }

            using var read = CreateContext();
            var reloaded = await read.Games.FirstAsync(g => g.Id == gameId);

            Assert.Equal(replacement, reloaded.PendingInvestorIds);
        }

        [Fact]
        public async Task MutatingPendingInvestorIdsInPlace_NowPersistsLikeItsNeighbours()
        {
            // This used to be silently lost: the [NotMapped] getter handed back a new list per read, so
            // the Add landed on a throwaway while the same call on PendingBattleDefenders worked. The
            // three properties now behave identically.
            var gameId = await SeedAsync();
            var added = Guid.NewGuid();

            using (var mutate = CreateContext())
            {
                var game = await mutate.Games.FirstAsync(g => g.Id == gameId);
                game.PendingInvestorIds.Add(added);
                await mutate.SaveChangesAsync();
            }

            using var read = CreateContext();
            var reloaded = await read.Games.FirstAsync(g => g.Id == gameId);

            Assert.Equal(2, reloaded.PendingInvestorIds.Count);
            Assert.Contains(added, reloaded.PendingInvestorIds);
        }

        [Fact]
        public async Task RowsWrittenInTheOldJsonFormat_StillReadBack()
        {
            // The migration is a bare RenameColumn, so live games carry JSON written by the old
            // System.Text.Json accessor - which lowercases its GUIDs, where EF's primitive collection
            // writes them uppercase. That difference must not matter on the way back in, or every
            // in-flight investor queue would be silently emptied by the upgrade.
            var gameId = Guid.NewGuid();
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();

            using (var context = CreateContext())
            {
                await context.Database.EnsureCreatedAsync();
                context.Games.Add(new Game { Id = gameId, Name = "Legacy Row", Status = GameStatus.InProgress });
                await context.SaveChangesAsync();
            }

            // Overwrite with exactly what the old accessor would have persisted.
            using (var cmd = _connection.CreateCommand())
            {
                // Matched by name: SQLite stores the Guid key as uppercase text, so a lowercase
                // ToString() would not match - the same case difference this test exists to check.
                cmd.CommandText = "UPDATE Games SET PendingInvestorIds = $json WHERE Name = $name";
                cmd.Parameters.AddWithValue("$json", System.Text.Json.JsonSerializer.Serialize(new List<Guid> { first, second }));
                cmd.Parameters.AddWithValue("$name", "Legacy Row");
                Assert.Equal(1, cmd.ExecuteNonQuery());
            }

            using var read = CreateContext();
            var reloaded = await read.Games.FirstAsync(g => g.Id == gameId);

            Assert.Equal(new[] { first, second }, reloaded.PendingInvestorIds);
        }
    }
}
