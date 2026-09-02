using System;
using System.Linq;

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
    /// The speeds the viewer's control steps through, slowest first.
    ///
    /// A media player offers a handful of named speeds rather than a continuous dial, because "2x" is
    /// what a viewer actually wants to express — a raw inter-action delay in seconds makes them do the
    /// arithmetic. Every entry is a multiple of <see cref="StepMs"/> and inside
    /// [<see cref="MinPacingMs"/>, <see cref="MaxPacingMs"/>], so <see cref="Normalize"/> returns each
    /// one unchanged and the displayed multiplier is always exactly what the server applied. A test
    /// asserts that rather than leaving it to inspection, since a preset off the grid would silently
    /// snap server-side and show a different number than the one clicked.
    /// </summary>
    public static readonly int[] PresetPacingsMs = { 10_000, 5_000, 2_500, 1_000, 500 };

    /// <summary>
    /// Playback speed relative to <see cref="DefaultPacingMs"/>: 2x is twice as fast, i.e. half the
    /// delay. Derived rather than stored alongside each preset so the label and the pace it describes
    /// cannot drift apart.
    /// </summary>
    public static double MultiplierFor(int pacingMs) =>
        pacingMs <= 0 ? 0 : DefaultPacingMs / (double)pacingMs;

    /// <summary>
    /// The next preset in the requested direction: <paramref name="direction"/> above zero means faster
    /// (a shorter delay), at or below zero means slower.
    ///
    /// Chosen by comparing against the current pace rather than by index arithmetic on the array, so a
    /// session sitting between presets — an older session, or a hand-crafted speed request — steps onto
    /// the neighbouring rung instead of snapping to a surprising one. Returns the current value
    /// unchanged when already at the end of the ladder, which is also how the caller decides whether to
    /// disable the button.
    /// </summary>
    public static int Step(int pacingMs, int direction) =>
        direction > 0
            ? PresetPacingsMs.Where(p => p < pacingMs).DefaultIfEmpty(pacingMs).Max()
            : PresetPacingsMs.Where(p => p > pacingMs).DefaultIfEmpty(pacingMs).Min();

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
