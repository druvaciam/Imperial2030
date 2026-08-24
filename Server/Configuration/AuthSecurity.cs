using Microsoft.AspNetCore.Identity;

namespace Imperial2030.Server.Configuration;

/// <summary>
/// Account-lockout policy for the sign-in surface. The matching request-rate policy lives in
/// RateLimitPolicies (it is one of several endpoint policies, not an auth-only concern).
///
/// Login previously called PasswordSignInAsync with lockoutOnFailure: false, so Identity's lockout
/// machinery was registered but never engaged, and no rate limiting existed anywhere in the pipeline.
/// Password guessing against /api/auth/login was therefore unbounded and unlogged.
///
/// The two mechanisms here cover different attacks and are both needed:
///   * Lockout caps guesses against ONE known account. It does nothing about an attacker sweeping many
///     usernames, and nothing about unbounded guest-token minting.
///   * The rate limiter (RateLimitPolicies.Auth) caps ALL /api/auth/* traffic per caller, which covers
///     both of those — and in turn blunts the denial-of-service that lockout introduces on its own (an
///     attacker deliberately locking a known victim out needs sustained request volume to keep doing it).
/// </summary>
public static class AuthSecurity
{
    /// <summary>Consecutive failures before an account is locked.</summary>
    public const int MaxFailedAccessAttempts = 5;

    /// <summary>
    /// Deliberately short. Lockout is itself a denial-of-service lever — anyone who knows a username can
    /// trip it — so this leans towards inconveniencing an attacker rather than stranding a real user.
    /// RateLimitPolicies.Auth is what makes sustained lock-out-the-victim attacks expensive.
    /// </summary>
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    public static void ConfigureLockout(IdentityOptions options)
    {
        options.Lockout.MaxFailedAccessAttempts = MaxFailedAccessAttempts;
        options.Lockout.DefaultLockoutTimeSpan = LockoutDuration;
        options.Lockout.AllowedForNewUsers = true;
    }
}
