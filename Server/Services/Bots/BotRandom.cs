namespace Imperial2030.Server.Services.Bots;

/// <summary>
/// The randomness bot decisions use. <see cref="System.Random.Shared"/> in a real game, as before; a
/// harness can seed it so the same game plays out the same way twice.
///
/// That matters for measurement, not for play. Comparing two bots over whole games means fighting this
/// game's variance: scores swing about 40 points game to game, so telling a 5-point difference from
/// nothing takes hundreds of games. Replay the same game with one decision changed and the swing is the
/// difference, not the deal - the comparison needs a tenth as many games to say anything.
///
/// Nothing about a real game changes: with no seed set, every caller gets Random.Shared exactly as it
/// did when each of them wrote it out by hand.
/// </summary>
public static class BotRandom
{
    private static readonly AsyncLocal<Random?> Seeded = new();

    /// <summary>The generator to draw from: a seeded one when a harness set it, Random.Shared otherwise.</summary>
    public static Random Current => Seeded.Value ?? Random.Shared;

    /// <summary>
    /// Makes every bot decision inside this scope draw from one seeded generator, so the same seed plays
    /// the same game. Flows into the bot turns started from the scope; dispose to put it back.
    /// </summary>
    public static IDisposable Use(int seed) => new Scope(new Random(seed));

    /// <summary>Leaves the scope, whatever happened inside it.</summary>
    private sealed class Scope : IDisposable
    {
        private readonly Random? _previous;

        public Scope(Random random)
        {
            _previous = Seeded.Value;
            Seeded.Value = random;
        }

        public void Dispose() => Seeded.Value = _previous;
    }
}
