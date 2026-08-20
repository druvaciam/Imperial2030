using System.Collections.Generic;

namespace Imperial2030.Shared.Models;

/// <summary>
/// A toast the server asks connected clients to display, sent as a resource key plus its arguments
/// rather than a pre-composed English sentence.
///
/// The server has no idea what language any given viewer has selected — localization in this app is
/// entirely client-side — so composing the sentence server-side would hard-code English for everyone.
/// Sending the key lets each client render it in its own language from its own resource files.
/// </summary>
public class LocalizedToast
{
    /// <summary>Resource key, from <see cref="Constants.ToastCodes"/>.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Positional arguments substituted into the resource string, in order.</summary>
    public List<ToastArg> Args { get; set; } = new();
}

/// <summary>
/// One argument for a <see cref="LocalizedToast"/>. Nations are flagged explicitly rather than
/// detected by the client, so a player whose display name happens to read like a nation is never
/// mistaken for one.
/// </summary>
public class ToastArg
{
    public string Value { get; set; } = string.Empty;

    /// <summary>True when <see cref="Value"/> is a Nation enum name the client should translate.</summary>
    public bool IsNation { get; set; }

    /// <summary>Plain text that is shown as-is (player names, amounts).</summary>
    public static ToastArg Text(string? value) => new() { Value = value ?? string.Empty };

    /// <summary>A nation, rendered in the viewer's language.</summary>
    public static ToastArg Of(Nation nation) => new() { Value = nation.ToString(), IsNation = true };
}
