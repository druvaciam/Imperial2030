using Imperial2030.Shared.Constants;
using Imperial2030.Shared.Models;
using Microsoft.Extensions.Localization;

namespace Imperial2030.Client.Services;

/// <summary>
/// Translates domain values into display text.
///
/// The domain identifiers themselves (<see cref="Nation"/>, territory IDs, rondel slot indices,
/// <see cref="UnitType"/>, phase names) stay English everywhere they are stored or transmitted —
/// they appear in the server's action-log metadata, in game exports, and in the RL state encoding,
/// so translating them at the source would corrupt all three. This class is the display layer that
/// maps a stable identifier to a localized string, leaving the identifier untouched.
///
/// Every lookup falls back to the existing English name rather than the raw resource key, so a
/// missing translation degrades to English instead of showing "Territory_Moscow" to a player.
/// </summary>
public class DisplayNameLocalizer
{
    private readonly IStringLocalizer<SharedResource> _localizer;

    public DisplayNameLocalizer(IStringLocalizer<SharedResource> localizer)
    {
        _localizer = localizer;
    }

    private string Lookup(string key, string englishFallback)
    {
        var value = _localizer[key];
        return value.ResourceNotFound ? englishFallback : value.Value;
    }

    public string Nation(Nation nation) => Lookup($"Nation_{nation}", nation.ToString());

    // Note: these must be fully qualified as Imperial2030.Shared.Models.* — this project also has an
    // Imperial2030.Client.Shared namespace (the layout components), so a bare "Shared.Models.X"
    // binds to Imperial2030.Client.Shared.Models and fails to resolve.

    /// <summary>Short nation code shown on the rondel's crowded nation markers (e.g. RUS, EU).</summary>
    public string NationAbbrev(Nation nation) => Lookup($"NationAbbrev_{nation}", nation switch
    {
        Imperial2030.Shared.Models.Nation.Russia => "RUS",
        Imperial2030.Shared.Models.Nation.China => "CHN",
        Imperial2030.Shared.Models.Nation.India => "IND",
        Imperial2030.Shared.Models.Nation.Brazil => "BRA",
        Imperial2030.Shared.Models.Nation.USA => "USA",
        Imperial2030.Shared.Models.Nation.Europe => "EU",
        _ => nation.ToString()
    });

    public string Nation(Nation? nation) => nation.HasValue ? Nation(nation.Value) : Lookup("Common_None", "None");

    /// <summary>
    /// Localizes a nation recorded in action metadata as a plain string rather than the enum
    /// (InvestmentMetadata.Nation is a string). Anything that does not parse as a known nation is
    /// passed through unchanged rather than dropped.
    /// </summary>
    public string NationFromName(string? nationName)
    {
        if (string.IsNullOrEmpty(nationName)) return string.Empty;
        return Enum.TryParse<Nation>(nationName, out var parsed) ? Nation(parsed) : nationName;
    }

    public string Territory(string? territoryId)
    {
        if (string.IsNullOrEmpty(territoryId)) return string.Empty;
        var english = TerritoryData.AllTerritories.FirstOrDefault(t => t.Id == territoryId)?.Name ?? territoryId;
        // Territory IDs contain '-' (e.g. "North-Africa"), which is legal in a .resx key name.
        return Lookup($"Territory_{territoryId}", english);
    }

    /// <summary>
    /// Rondel slot label. Keyed by slot index rather than by name because Production and Maneuver
    /// each occupy two slots, and callers dispatch on the index.
    /// </summary>
    public string RondelSlot(int slotIndex) => Lookup($"Rondel_Slot_{slotIndex}", RondelData.GetSlotName(slotIndex));

    /// <summary>Standalone unit label, capitalised — dropdown options, unit lists, phrase starts.</summary>
    public string UnitType(UnitType unitType) => Lookup($"UnitType_{unitType}", unitType.ToString());

    /// <summary>
    /// Lowercase unit noun for mid-sentence use. Separate from <see cref="UnitType"/> because that
    /// one doubles as a standalone label: reusing it inside a sentence wrongly capitalises the noun.
    /// </summary>
    public string UnitTypeLower(UnitType unitType) =>
        Lookup($"UnitTypeLower_{unitType}", unitType.ToString().ToLowerInvariant());

    /// <summary>Single-letter unit marker drawn on the map ("A"/"F").</summary>
    public string UnitGlyph(UnitType unitType) => Lookup($"UnitGlyph_{unitType}",
        unitType == Imperial2030.Shared.Models.UnitType.Fleet ? "F" : "A");

    public string GameStatus(GameStatus status) => Lookup($"GameStatus_{status}", status.ToString());

    public string ManeuverPhase(ManeuverPhase phase) => Lookup($"ManeuverPhase_{phase}", phase.ToString());

    /// <summary>
    /// Singular form of a maneuver phase, for prompts that address one unit ("Move this Fleet").
    /// A dedicated key rather than trimming a plural 's' — that trick is English-only.
    /// </summary>
    public string ManeuverPhaseSingular(ManeuverPhase phase) => Lookup($"ManeuverPhaseSingular_{phase}",
        phase.ToString().TrimEnd('s'));

    /// <summary>
    /// Phase name as recorded in the action log's metadata. The server bakes English "Fleets",
    /// "Armies" or "Turn" into <c>PhaseMetadata.PhaseName</c>; this maps that closed set for display
    /// without needing the log format to change.
    /// </summary>
    public string PhaseName(string? serverPhaseName) =>
        string.IsNullOrEmpty(serverPhaseName) ? string.Empty : Lookup($"PhaseName_{serverPhaseName}", serverPhaseName);

    /// <summary>
    /// Bot strategy label. The identifier doubles as the wire value sent to the server when adding a
    /// bot, so only the label is translated — the ID stays English.
    /// </summary>
    public string BotType(string botTypeId) => Lookup($"BotType_{botTypeId}", botTypeId);
}
