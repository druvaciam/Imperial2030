using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Identity;

namespace Imperial2030.Server.Configuration;

/// <summary>
/// Brute-force protection for the sign-in surface, in one place.
///
/// Login previously called PasswordSignInAsync with lockoutOnFailure: false, so Identity's lockout
/// machinery was registered but never engaged, and no rate limiting existed anywhere in the pipeline.
/// Password guessing against /api/auth/login was therefore unbounded and unlogged.
///
/// The two mechanisms here cover different attacks and are both needed:
///   * Lockout caps guesses against ONE known account. It does nothing about an attacker sweeping many
///     usernames, and nothing about unbounded guest-token minting.
///   * The rate limiter caps ALL /api/auth/* traffic per caller, which covers both of those — and in turn
///     blunts the denial-of-service that lockout introduces on its own (an attacker deliberately locking
///     a known victim out needs sustained request volume to keep doing it).
/// </summary>
public static class AuthSecurity
{
    // --- Account lockout -------------------------------------------------------------------------

    /// <summary>Consecutive failures before an account is locked.</summary>
    public const int MaxFailedAccessAttempts = 5;

    /// <summary>
    /// Deliberately short. Lockout is itself a denial-of-service lever — anyone who knows a username can
    /// trip it — so this leans towards inconveniencing an attacker rather than stranding a real user.
    /// The rate limiter below is what makes sustained lock-out-the-victim attacks expensive.
    /// </summary>
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    public static void ConfigureLockout(IdentityOptions options)
    {
        options.Lockout.MaxFailedAccessAttempts = MaxFailedAccessAttempts;
        options.Lockout.DefaultLockoutTimeSpan = LockoutDuration;
        options.Lockout.AllowedForNewUsers = true;
    }

    // --- Rate limiting ---------------------------------------------------------------------------

    /// <summary>Referenced by AuthController's [EnableRateLimiting] attribute.</summary>
    public const string AuthPolicyName = "auth";

    /// <summary>
    /// Generous for a human (a real sign-in flow costs one or two requests) and still cuts a brute-force
    /// attempt from millions of attempts a day to tens of thousands, on top of lockout. Kept generous on
    /// purpose: behind a reverse proxy every caller can share one partition key (see ResolvePartitionKey),
    /// and throttling all users at once would be a worse bug than the one being fixed.
    /// </summary>
    public const int DefaultAuthPermitLimit = 20;

    public const int DefaultAuthWindowSeconds = 60;

    private const string PermitLimitConfigKey = "RateLimiting:AuthPermitLimit";
    private const string WindowSecondsConfigKey = "RateLimiting:AuthWindowSeconds";

    /// <summary>
    /// Partition key for the auth limiter.
    ///
    /// Uses the transport-level remote address only. X-Forwarded-For is deliberately NOT trusted here: a
    /// client can set that header to anything, so honouring it would hand an attacker a fresh bucket per
    /// request and make the limiter decorative.
    ///
    /// Caveat worth knowing at deploy time: behind a reverse proxy that does not rewrite the connection
    /// address (nginx on the VPS, for instance), every caller collapses into one partition and shares a
    /// single budget. That is why the default limit is generous rather than tight. To get true per-client
    /// limiting there, configure ForwardedHeaders with an explicit KnownProxies/KnownNetworks allow-list
    /// so RemoteIpAddress becomes the real client address — an allow-list is what makes the header
    /// trustworthy, and it is deployment-specific, so it is not configured here.
    /// </summary>
    private static string ResolvePartitionKey(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    public static IServiceCollection AddAuthRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        int permitLimit = configuration.GetValue<int?>(PermitLimitConfigKey) ?? DefaultAuthPermitLimit;
        int windowSeconds = configuration.GetValue<int?>(WindowSecondsConfigKey) ?? DefaultAuthWindowSeconds;
        var window = TimeSpan.FromSeconds(windowSeconds);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(AuthPolicyName, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: ResolvePartitionKey(context),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = permitLimit,
                        Window = window,
                        // Refuse immediately instead of parking the request. Queuing sign-in attempts
                        // would hold connections open for an attacker at no cost to them.
                        QueueLimit = 0
                    }));

            options.OnRejected = (context, cancellationToken) =>
            {
                // Tell an honest client when to come back. RetryAfter is only populated by limiters that
                // can compute it (the fixed window can), so fall back to the configured window.
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var metadata)
                    ? metadata
                    : window;

                context.HttpContext.Response.Headers.RetryAfter =
                    ((int)retryAfter.TotalSeconds).ToString(NumberFormatInfo.InvariantInfo);

                return ValueTask.CompletedTask;
            };
        });

        return services;
    }
}
