using System.Globalization;
using System.Threading.RateLimiting;

namespace Imperial2030.Server.Configuration;

/// <summary>
/// Named rate-limit policies and their registration, in one place.
///
/// Applied per-endpoint via [EnableRateLimiting] rather than globally, so ordinary gameplay traffic is
/// never throttled — a busy game becoming unplayable would be a worse outcome than the abuse being
/// mitigated.
/// </summary>
public static class RateLimitPolicies
{
    /// <summary>Sign-in surface: login, register, guest-login. Brute force and token-minting spam.</summary>
    public const string Auth = "auth";

    /// <summary>
    /// Replay session creation. ReplaySessionManager's caps already bound how many sessions one caller can
    /// HOLD, but not how fast they can churn them: start, stop, start again costs a full source-game load
    /// and reseed every cycle while never exceeding the cap. This bounds the rate as well as the count.
    /// </summary>
    public const string Replay = "replay";

    public const int DefaultAuthPermitLimit = 20;
    public const int DefaultAuthWindowSeconds = 60;

    /// <summary>Lower than auth: starting a replay is far more expensive than a sign-in attempt.</summary>
    public const int DefaultReplayPermitLimit = 10;
    public const int DefaultReplayWindowSeconds = 60;

    private const string AuthPermitLimitKey = "RateLimiting:AuthPermitLimit";
    private const string AuthWindowSecondsKey = "RateLimiting:AuthWindowSeconds";
    private const string ReplayPermitLimitKey = "RateLimiting:ReplayPermitLimit";
    private const string ReplayWindowSecondsKey = "RateLimiting:ReplayWindowSeconds";

    /// <summary>
    /// Partition key for every policy here.
    ///
    /// Uses the transport-level remote address only. X-Forwarded-For is deliberately NOT trusted: a client
    /// can set that header to anything, so honouring it would hand an attacker a fresh bucket per request
    /// and make the limiter decorative.
    ///
    /// Caveat worth knowing at deploy time: behind a reverse proxy that does not rewrite the connection
    /// address (nginx on the VPS, for instance), every caller collapses into one partition and shares a
    /// single budget. That is why the defaults are generous rather than tight. To get true per-client
    /// limiting there, configure ForwardedHeaders with an explicit KnownProxies/KnownNetworks allow-list so
    /// RemoteIpAddress becomes the real client address — an allow-list is what makes the header
    /// trustworthy, and it is deployment-specific, so it is not configured here.
    /// </summary>
    internal static string ResolvePartitionKey(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    public static IServiceCollection AddAppRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var authWindow = TimeSpan.FromSeconds(
            configuration.GetValue<int?>(AuthWindowSecondsKey) ?? DefaultAuthWindowSeconds);
        int authPermits = configuration.GetValue<int?>(AuthPermitLimitKey) ?? DefaultAuthPermitLimit;

        var replayWindow = TimeSpan.FromSeconds(
            configuration.GetValue<int?>(ReplayWindowSecondsKey) ?? DefaultReplayWindowSeconds);
        int replayPermits = configuration.GetValue<int?>(ReplayPermitLimitKey) ?? DefaultReplayPermitLimit;

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            AddFixedWindowPolicy(options, Auth, authPermits, authWindow);
            AddFixedWindowPolicy(options, Replay, replayPermits, replayWindow);

            options.OnRejected = (context, _) =>
            {
                // Tell an honest client when to come back. RetryAfter is only populated by limiters that
                // can compute it (a fixed window can), so fall back to the policy's own window.
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var metadata)
                    ? metadata
                    : authWindow;

                context.HttpContext.Response.Headers.RetryAfter =
                    ((int)retryAfter.TotalSeconds).ToString(NumberFormatInfo.InvariantInfo);

                return ValueTask.CompletedTask;
            };
        });

        return services;
    }

    private static void AddFixedWindowPolicy(
        Microsoft.AspNetCore.RateLimiting.RateLimiterOptions options, string name, int permitLimit, TimeSpan window)
    {
        options.AddPolicy(name, context =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: ResolvePartitionKey(context),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = window,
                    // Refuse immediately instead of parking the request. Queuing would hold connections
                    // open on the caller's behalf at no cost to them.
                    QueueLimit = 0
                }));
    }
}
