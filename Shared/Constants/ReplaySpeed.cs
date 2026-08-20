using System;

namespace Imperial2030.Shared.Constants;

/// <summary>
/// The delay between visibly-applied actions during replay playback, and the range a viewer may
/// choose from. Shared so the client's speed control and the server's validation cannot drift apart.
/// </summary>
public static class ReplaySpeed
{
    /// <summary>Delay a session starts at — the original fixed pace, so existing behaviour is unchanged.</summary>
    public const int DefaultPacingMs = 5_000;

    /// <summary>Fastest setting (shortest delay).</summary>
    public const int MinPacingMs = 500;

    /// <summary>Slowest setting (longest delay).</summary>
    public const int MaxPacingMs = 10_000;

    /// <summary>Granularity of a single Slower/Faster step.</summary>
    public const int StepMs = 500;

    /// <summary>
    /// Snaps an arbitrary value onto the allowed grid: rounded to the nearest <see cref="StepMs"/> and
    /// clamped into [<see cref="MinPacingMs"/>, <see cref="MaxPacingMs"/>]. Applied server-side so a
    /// hand-crafted request cannot set a 0ms pace (which would spin the replay loop) or an absurdly
    /// long one that pins a session open against the idle sweep.
    /// </summary>
    public static int Normalize(int pacingMs)
    {
        // AwayFromZero explicitly: Math.Round's default is banker's rounding, which sends an exact
        // midpoint to the nearest *even* multiple — 3250 would snap down to 3000 but 3750 up to 4000.
        // Inconsistent-looking for a value a user is stepping through, so midpoints always round up.
        var snapped = (int)(Math.Round(pacingMs / (double)StepMs, MidpointRounding.AwayFromZero) * StepMs);
        return Math.Clamp(snapped, MinPacingMs, MaxPacingMs);
    }
}
