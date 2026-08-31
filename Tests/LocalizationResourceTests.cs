using System.Xml.Linq;
using Xunit;

namespace Imperial2030.Tests;

/// <summary>
/// Guards the localization resource files against drift. A key added to the English .resx but not
/// its Belarusian counterpart would otherwise silently fall back to English in the running app —
/// invisible until a Belarusian speaker hits that screen. These tests fail the build instead.
///
/// The files are read from disk rather than through a project reference: Tests references only
/// Server and Shared, and adding a reference to a Blazor WebAssembly project just to read resources
/// would be a heavier coupling than the check is worth.
/// </summary>
public class LocalizationResourceTests
{
    private const string TranslatedCultureSuffix = ".be.resx";

    /// <summary>
    /// Walks up from the test assembly to the repository root (identified by the solution file), so
    /// this works from whatever output directory the test host uses, on Windows and Linux alike.
    /// </summary>
    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Imperial2030.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir != null, "Could not locate the repository root (no Imperial2030.sln found above the test assembly).");
        return dir!.FullName;
    }

    private static string ResourcesDirectory() => Path.Combine(FindRepositoryRoot(), "Client", "Resources");

    private static HashSet<string> ReadKeys(string resxPath)
    {
        var doc = XDocument.Load(resxPath);
        return doc.Root!
            .Elements("data")
            .Select(e => e.Attribute("name")?.Value)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToHashSet();
    }

    /// <summary>Every neutral (English) .resx paired with its Belarusian translation file.</summary>
    public static IEnumerable<object[]> ResourceFilePairs()
    {
        var root = ResourcesDirectory();
        foreach (var neutral in Directory.GetFiles(root, "*.resx", SearchOption.AllDirectories)
                                         .Where(f => !f.EndsWith(TranslatedCultureSuffix))
                                         .OrderBy(f => f))
        {
            yield return new object[] { neutral, neutral[..^".resx".Length] + TranslatedCultureSuffix };
        }
    }

    [Fact]
    public void ResourceFilesExist()
    {
        var pairs = ResourceFilePairs().ToList();
        Assert.NotEmpty(pairs);

        foreach (var pair in pairs)
        {
            var translated = (string)pair[1];
            Assert.True(File.Exists(translated),
                $"Missing Belarusian resource file '{Path.GetFileName(translated)}' for '{Path.GetFileName((string)pair[0])}'.");
        }
    }

    [Theory]
    [MemberData(nameof(ResourceFilePairs))]
    public void EveryEnglishKeyHasATranslation(string neutralPath, string translatedPath)
    {
        Assert.True(File.Exists(translatedPath), $"Missing translation file: {translatedPath}");

        var english = ReadKeys(neutralPath);
        var belarusian = ReadKeys(translatedPath);

        var untranslated = english.Except(belarusian).OrderBy(k => k).ToList();
        Assert.True(untranslated.Count == 0,
            $"{Path.GetFileName(neutralPath)} has keys with no Belarusian translation: {string.Join(", ", untranslated)}");

        // Orphans mean a key was renamed or removed in English but left behind in the translation,
        // which is dead weight a translator will keep maintaining for no reason.
        var orphaned = belarusian.Except(english).OrderBy(k => k).ToList();
        Assert.True(orphaned.Count == 0,
            $"{Path.GetFileName(translatedPath)} has keys that no longer exist in English: {string.Join(", ", orphaned)}");
    }

    /// <summary>
    /// Server-pushed toasts name their resource key through ToastCodes; if the client has no entry
    /// for one, the toast renders as the raw key. This catches that at build time.
    /// </summary>
    [Fact]
    public void EveryToastCodeHasAResourceEntry()
    {
        var gameRoomResx = Path.Combine(ResourcesDirectory(), "Pages", "GameRoom.resx");
        var keys = ReadKeys(gameRoomResx);

        var toastCodes = typeof(Imperial2030.Shared.Constants.ToastCodes)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.NotEmpty(toastCodes);

        var missing = toastCodes.Where(code => !keys.Contains(code)).OrderBy(c => c).ToList();
        Assert.True(missing.Count == 0,
            $"ToastCodes with no entry in GameRoom.resx: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// Territory_* keys whose territory is deliberately NOT in TerritoryData, and which must survive a
    /// tidy-up that prunes "orphan" resource keys.
    ///
    /// Switzerland is drawn on the map but is not a playable space, so it has no TerritoryData entry.
    /// GameMap.razor still labels it via DisplayNameLocalizer.Territory("Switzerland"), which falls back
    /// to the raw territory id when TerritoryData has no definition and then looks up
    /// "Territory_Switzerland". If that key were removed as unused, Lookup would report ResourceNotFound
    /// and the tooltip would silently render the English id in every language - the exact class of
    /// invisible regression the rest of this file exists to prevent.
    /// </summary>
    [Theory]
    [InlineData("Territory_Switzerland")]
    public void MapOnlyTerritoryLabelsAreTranslatedInEveryCulture(string key)
    {
        var root = ResourcesDirectory();

        foreach (var resx in Directory.GetFiles(root, "SharedResource*.resx").OrderBy(f => f))
        {
            Assert.True(ReadKeys(resx).Contains(key),
                $"{key} is missing from {Path.GetFileName(resx)}. It has no TerritoryData entry by design, " +
                "but GameMap.razor still renders it as a tooltip - see this test's remarks before deleting it.");
        }
    }
}
