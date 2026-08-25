using System.Security.Cryptography;

namespace Imperial2030.Server.Helpers;

/// <summary>
/// Join codes for private games.
///
/// These are a security boundary — the code is the only thing standing between a private game and anyone
/// who wants in — so they come from <see cref="RandomNumberGenerator"/> rather than <c>new Random()</c>.
/// A per-call <c>new Random()</c> is doubly wrong here: its output is predictable from the seed, and two
/// games created in the same instant could be handed the same code.
///
/// The alphabet is uppercase letters and digits, which is 36^6 ≈ 2.2 billion codes. That is a keyspace
/// worth protecting with rate limiting rather than length alone (see the auth rate limits).
/// </summary>
public static class JoinCodeGenerator
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    public const int CodeLength = 6;

    /// <summary>A fresh, uniformly distributed join code.</summary>
    public static string Generate()
    {
        // GetString samples without modulo bias, which a naive `bytes[i] % Alphabet.Length` would
        // introduce (256 is not a multiple of 36, so the first four letters would come up slightly more
        // often than the rest).
        return RandomNumberGenerator.GetString(Alphabet, CodeLength);
    }
}
