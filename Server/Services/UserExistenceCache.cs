using Microsoft.Extensions.Caching.Memory;

namespace Imperial2030.Server.Services;

/// <summary>
/// Short-lived memo of "does this user id still exist in the store", for the
/// <c>JwtBearerEvents.OnTokenValidated</c> check in <c>Program.cs</c>.
///
/// That check has to stay - a token outlives the account it was minted for, and rejecting a deleted
/// user's token is the only revocation this app has - but running it as a user-store round-trip on
/// <em>every</em> authenticated request is what made it expensive: the replay view polls
/// <c>GetReplayState</c> every 400ms (<c>Client/Pages/GameRoom.razor</c>'s poll loop, mirrored by
/// <c>VueReplayViewer</c>'s <c>POLL_INTERVAL_MS</c>), and each of those polls paid for a lookup.
///
/// The trade-off is explicit: a user deleted mid-session keeps working until their cache entry expires,
/// so <see cref="DefaultTtl"/> is the revocation lag. It is deliberately far shorter than the token
/// lifetime, which is the coarser bound that already applies.
/// </summary>
public sealed class UserExistenceCache
{
    /// <summary>
    /// How long an existence answer is trusted. At the replay view's 400ms poll this serves ~75 requests
    /// per store lookup, while keeping the window in which a deleted account still authenticates well
    /// under a minute.
    /// </summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(30);

    private readonly IMemoryCache _cache;
    private readonly TimeSpan _ttl;

    public UserExistenceCache(IMemoryCache cache) : this(cache, DefaultTtl) { }

    public UserExistenceCache(IMemoryCache cache, TimeSpan ttl)
    {
        _cache = cache;
        _ttl = ttl;
    }

    /// <summary>
    /// Returns whether <paramref name="userId"/> exists, consulting <paramref name="lookup"/> only when
    /// no unexpired answer is held. Negative answers are cached too, so a token for a deleted user does
    /// not keep costing a lookup on every request it is replayed on.
    /// </summary>
    public async Task<bool> ExistsAsync(string userId, Func<string, Task<bool>> lookup)
    {
        // A non-positive TTL disables caching outright and restores the original every-request lookup.
        if (_ttl <= TimeSpan.Zero) return await lookup(userId);

        var key = CacheKey(userId);
        if (_cache.TryGetValue(key, out bool cached)) return cached;

        bool exists = await lookup(userId);
        _cache.Set(key, exists, _ttl);
        return exists;
    }

    private static string CacheKey(string userId) => "user-exists:" + userId;
}
