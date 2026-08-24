using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Imperial2030.Server.Configuration;

/// <summary>
/// The single resolved source of truth for JWT issuance and validation.
///
/// Every value here was previously a literal duplicated between Program.cs (which validates tokens) and
/// AuthController (which issues them) — including the signing key, which carried a hardcoded fallback
/// committed to the repository in both files. Two consequences, both real:
///
///   * `Jwt:Key` is set in no appsettings file, and the Azure deploy workflow configures no app settings,
///     so any environment that had not set it manually was signing and accepting tokens with a key that
///     is public in git history. Anyone holding it can forge a token for any user. Nothing distinguished
///     "properly configured" from "running on the committed default", so the exposure was silent.
///   * Issuer/audience/lifetime existed in three places. If any copy drifted, every issued token would
///     silently fail validation — the same class of split-brain failure that broke guest login.
///
/// Resolved once at startup and registered as a singleton, so issuance and validation cannot disagree.
/// </summary>
public sealed class JwtOptions
{
    public const string Issuer = "Imperial2030Server";
    public const string Audience = "Imperial2030Client";

    /// <summary>
    /// HMAC-SHA256 requires a 256-bit key. Enforced at startup so an undersized key fails loudly here
    /// rather than at first sign-in, and measured in BYTES because a short multi-byte string can satisfy
    /// a character-count check while still being too small.
    /// </summary>
    public const int MinimumKeyBytes = 32;

    public static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(1);

    private const string ConfigKeyName = "Jwt:Key";

    /// <summary>
    /// SHA-256 of the signing key that shipped in this repository's git history. Stored as a hash rather
    /// than the plaintext so the compromised value is not reintroduced into a shipped binary, while still
    /// letting startup refuse it outright — a deployment that copied it out of the old source, or that
    /// simply never set anything and inherited it, must not keep running on it.
    /// </summary>
    private const string LeakedLegacyKeySha256 = "268d502006fcd30d7c027975be259ffb99634ab95816740a8fb8a2fb9ee213a3";

    public required string Key { get; init; }

    private SymmetricSecurityKey? _signingKey;

    /// <summary>Cached so it is allocated once, not per token issued or validated.</summary>
    public SymmetricSecurityKey SigningKey => _signingKey ??= new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key));

    /// <summary>
    /// Resolves the signing key from configuration, failing fast outside Development.
    ///
    /// Development falls back to a per-process random key rather than any literal, so no usable secret
    /// exists in the repository at all. The trade-off is that tokens do not survive a server restart
    /// locally — set a stable key via user-secrets or the Jwt__Key environment variable to avoid that.
    /// The warning is returned rather than logged because this runs before the host (and therefore the
    /// logging pipeline) is built; the caller logs it through ILogger once the app is available.
    /// </summary>
    public static (JwtOptions Options, string? Warning) Resolve(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration[ConfigKeyName];
        bool isDevelopment = environment.IsDevelopment();

        if (string.IsNullOrWhiteSpace(configured))
        {
            if (!isDevelopment)
            {
                throw new InvalidOperationException(
                    $"{ConfigKeyName} is not configured. Set it to a random secret of at least " +
                    $"{MinimumKeyBytes} bytes before starting the server outside Development " +
                    "(e.g. `Jwt__Key` as an environment variable or an Azure App Service application " +
                    "setting; generate one with `openssl rand -base64 32`). Refusing to start rather " +
                    "than fall back to a shared default.");
            }

            return (new JwtOptions { Key = GenerateEphemeralKey() },
                $"{ConfigKeyName} is not configured; generated a random signing key for this process. " +
                "Tokens will stop working when the server restarts. Set a stable key via user-secrets " +
                "(`dotnet user-secrets set \"Jwt:Key\" \"...\"`) or the Jwt__Key environment variable.");
        }

        if (Encoding.UTF8.GetByteCount(configured) < MinimumKeyBytes)
        {
            throw new InvalidOperationException(
                $"{ConfigKeyName} must be at least {MinimumKeyBytes} bytes for HMAC-SHA256 signing; " +
                $"the configured value is {Encoding.UTF8.GetByteCount(configured)} bytes.");
        }

        if (IsLeakedLegacyKey(configured))
        {
            throw new InvalidOperationException(
                $"{ConfigKeyName} is set to the default key that was committed to this repository's " +
                "source history. It is no longer secret — anyone can forge tokens for any user with it. " +
                "Generate a new random key (`openssl rand -base64 32`) and rotate every deployment that " +
                "may have been running on the old value.");
        }

        return (new JwtOptions { Key = configured }, null);
    }

    private static bool IsLeakedLegacyKey(string candidate)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(candidate))).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(hash),
            Encoding.UTF8.GetBytes(LeakedLegacyKeySha256));
    }

    private static string GenerateEphemeralKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(MinimumKeyBytes));
}
