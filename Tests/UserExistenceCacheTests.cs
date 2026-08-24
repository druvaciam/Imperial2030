using System;
using System.Threading.Tasks;
using Imperial2030.Server.Services;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Imperial2030.Tests
{
    /// <summary>
    /// JwtBearerEvents.OnTokenValidated checks that the token's user still exists, which meant a user-store
    /// round-trip on EVERY authenticated request - including the replay poll that GameRoom.razor runs every
    /// 400ms. These cover the cache that removes that per-request hit without dropping the check.
    /// </summary>
    public class UserExistenceCacheTests
    {
        private static UserExistenceCache Build(TimeSpan ttl) =>
            new UserExistenceCache(new MemoryCache(new MemoryCacheOptions()), ttl);

        [Fact]
        public async Task ExistsAsync_SecondCallWithinTtl_DoesNotHitTheStore()
        {
            var cache = Build(TimeSpan.FromMinutes(1));
            int lookups = 0;

            Task<bool> Lookup(string _) { lookups++; return Task.FromResult(true); }

            Assert.True(await cache.ExistsAsync("user-1", Lookup));
            Assert.True(await cache.ExistsAsync("user-1", Lookup));
            Assert.True(await cache.ExistsAsync("user-1", Lookup));

            Assert.Equal(1, lookups);
        }

        [Fact]
        public async Task ExistsAsync_CachesNegativeResultsToo()
        {
            // Otherwise a token for a deleted user - the exact case the check exists to catch - still costs
            // a store round-trip on every request it is replayed on.
            var cache = Build(TimeSpan.FromMinutes(1));
            int lookups = 0;

            Task<bool> Lookup(string _) { lookups++; return Task.FromResult(false); }

            Assert.False(await cache.ExistsAsync("ghost", Lookup));
            Assert.False(await cache.ExistsAsync("ghost", Lookup));

            Assert.Equal(1, lookups);
        }

        [Fact]
        public async Task ExistsAsync_DoesNotShareEntriesBetweenUsers()
        {
            var cache = Build(TimeSpan.FromMinutes(1));

            Assert.True(await cache.ExistsAsync("real", _ => Task.FromResult(true)));
            Assert.False(await cache.ExistsAsync("ghost", _ => Task.FromResult(false)));
            // Both answers must still be their own.
            Assert.True(await cache.ExistsAsync("real", _ => Task.FromResult(false)));
            Assert.False(await cache.ExistsAsync("ghost", _ => Task.FromResult(true)));
        }

        [Fact]
        public async Task ExistsAsync_WithNonPositiveTtl_AlwaysHitsTheStore()
        {
            // The escape hatch: a zero/negative TTL disables caching outright, so the check keeps its
            // original every-request semantics for anyone who wants them back.
            var cache = Build(TimeSpan.Zero);
            int lookups = 0;

            Task<bool> Lookup(string _) { lookups++; return Task.FromResult(true); }

            await cache.ExistsAsync("user-1", Lookup);
            await cache.ExistsAsync("user-1", Lookup);

            Assert.Equal(2, lookups);
        }
    }
}
