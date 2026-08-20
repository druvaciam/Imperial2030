using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Imperial2030.Client.Services;

/// <summary>
/// One supported UI language: the culture name used for resource lookup, and the label shown in the
/// language dropdown (always written in that language itself, so a speaker can find their own).
/// </summary>
public record SupportedCulture(string Name, string DisplayName);

/// <summary>
/// Owns the client's UI language: which cultures exist, which one is active, and how switching works.
/// </summary>
public class LanguageService
{
    /// <summary>localStorage key holding the user's chosen culture name.</summary>
    public const string StorageKey = "Imperial2030.culture";

    /// <summary>Culture used when the user has never chosen one.</summary>
    public const string DefaultCulture = "en";

    /// <summary>
    /// The single source of truth for which languages exist. Adding a language is a .resx pair per
    /// component plus one entry here — nothing else enumerates cultures.
    /// </summary>
    public static readonly IReadOnlyList<SupportedCulture> SupportedCultures = new[]
    {
        new SupportedCulture("en", "English"),
        new SupportedCulture("be", "Беларуская"),
    };

    private readonly IJSRuntime _js;
    private readonly NavigationManager _navigation;

    public LanguageService(IJSRuntime js, NavigationManager navigation)
    {
        _js = js;
        _navigation = navigation;
    }

    /// <summary>The culture currently in effect for resource lookup.</summary>
    public static CultureInfo CurrentCulture => CultureInfo.CurrentUICulture;

    /// <summary>
    /// Reads the stored culture, falling back to <see cref="DefaultCulture"/> when nothing is stored
    /// or the stored value is no longer a supported culture. Called once from Program.cs before the
    /// host runs, because satellite assemblies are fetched for whatever culture is set at that moment.
    /// </summary>
    public static async Task<CultureInfo> ResolveStartupCultureAsync(IJSRuntime js)
    {
        string? stored = null;
        try
        {
            stored = await js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
        }
        catch (Exception)
        {
            // Storage can be unavailable (private mode, blocked cookies). Fall through to the default.
        }

        var name = SupportedCultures.Any(c => c.Name == stored) ? stored! : DefaultCulture;
        return CultureInfo.GetCultureInfo(name);
    }

    /// <summary>
    /// Applies a culture for the rest of this app instance. Sets both the Default* fallbacks (which
    /// new threads inherit) and the current values (which the CurrentCulture getter consults first).
    /// </summary>
    public static void ApplyCulture(CultureInfo culture)
    {
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    /// <summary>
    /// Persists the chosen language and reloads the app so it takes effect.
    ///
    /// The reload is required, not a preference: .resx compiles to satellite assemblies which Blazor
    /// WebAssembly fetches and loads exactly once during startup, for the culture chain in effect at
    /// that moment. Setting the culture afterwards triggers no second fetch — ResourceManager would
    /// silently fall back to the neutral (English) resources with no error. Reloading re-runs
    /// Program.cs, which reads this stored value back before the host starts.
    ///
    /// Game state survives: it is all server-side, and GameRoom re-fetches it and re-establishes the
    /// SignalR connection on load.
    /// </summary>
    public async Task SetCultureAsync(string cultureName)
    {
        if (!SupportedCultures.Any(c => c.Name == cultureName)) return;
        if (CurrentCulture.Name == cultureName) return;

        await _js.InvokeVoidAsync("localStorage.setItem", StorageKey, cultureName);
        _navigation.NavigateTo(_navigation.Uri, forceLoad: true);
    }
}
