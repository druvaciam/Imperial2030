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
    ///   * `PendingInvestorIds` is `[NotMapped]`, backed by a hand-serialised `PendingInvestorIdsJson`
    ///     column whose getter deserialises a FRESH list on every read — so in-place mutation is a silent
    ///     no-op and callers must reassign.
    ///
    /// Production code mutates the first two in place (`.Clear()`) and only ever reassigns the third.
    /// These run against real SQLite because that difference lives in the provider's change tracking, and
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
        public async Task MutatingPendingInvestorIdsInPlace_IsSilentlyLost()
        {
            // Documents the trap rather than endorsing it: the getter deserialises a new list per read, so
            // this Add lands on a throwaway. The two primitive collections above accept the same call.
            // Anyone reaching for `.Add` here needs to know it does nothing - hence this test.
            var gameId = await SeedAsync();

            using (var mutate = CreateContext())
            {
                var game = await mutate.Games.FirstAsync(g => g.Id == gameId);
                game.PendingInvestorIds.Add(Guid.NewGuid());
                await mutate.SaveChangesAsync();
            }

            using var read = CreateContext();
            var reloaded = await read.Games.FirstAsync(g => g.Id == gameId);

            Assert.Single(reloaded.PendingInvestorIds);
        }
    }
}
