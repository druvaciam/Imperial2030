using Microsoft.Extensions.Logging.Abstractions;

namespace Imperial2030.Server.Services;

/// <summary>
/// The bot types a game may be given: the built-in strategies, plus whatever trained RL models were
/// deployed alongside the server as `.onnx` files.
///
/// Discovered exactly once. The scan used to run on every call — from `AddBot`, and from the
/// `[AllowAnonymous]` `available-bots` endpoint, which let any anonymous caller drive directory
/// enumeration at will. The models ship with the deployment and do not appear at runtime, so there is
/// nothing to re-check: a new model arrives with a new deployment, which restarts the process anyway.
/// </summary>
public sealed class BotTypeCatalog
{
    /// <summary>Strategies compiled into the server, always offered regardless of what is on disk.</summary>
    public static readonly string[] BuiltInBotTypes = { "Default", "Aggressive", "Friendly", "Greedy", "Random" };

    private const string DefaultRlModel = "RL.onnx";
    private const string LegacyRlModel = "imperial_ppo_bot.onnx";
    private const string RlBotType = "RL";

    private readonly Lazy<IReadOnlyList<string>> _available;

    /// <param name="modelDirectory">
    /// Where to look for `.onnx` models; defaults to the deployment directory. Overridable so the scan
    /// can be exercised against a fixture directory instead of whatever the test host happens to ship.
    /// </param>
    public BotTypeCatalog(ILogger<BotTypeCatalog>? logger = null, string? modelDirectory = null)
    {
        var directory = modelDirectory ?? AppContext.BaseDirectory;
        var log = (ILogger?)logger ?? NullLogger.Instance;

        // ExecutionAndPublication: the scan runs once even if several requests race for it.
        _available = new Lazy<IReadOnlyList<string>>(() => Discover(directory, log),
                                                     LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public IReadOnlyList<string> Available => _available.Value;

    private static IReadOnlyList<string> Discover(string directory, ILogger logger)
    {
        var botTypes = new List<string>(BuiltInBotTypes);

        try
        {
            if (File.Exists(Path.Combine(directory, LegacyRlModel)) || File.Exists(Path.Combine(directory, DefaultRlModel)))
            {
                botTypes.Add(RlBotType);
            }

            foreach (var file in Directory.GetFiles(directory, "*.onnx"))
            {
                var name = Path.GetFileNameWithoutExtension(file);

                // The two names above are the plain "RL" bot, already added; anything else starting with
                // RL is a separately trained model (RL-2, RL-3, ...) offered under its own name.
                if (name.Equals(LegacyRlModel[..^5], StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals(RlBotType, StringComparison.OrdinalIgnoreCase)) continue;
                if (!name.StartsWith(RlBotType, StringComparison.OrdinalIgnoreCase)) continue;

                botTypes.Add(name);
            }
        }
        catch (Exception ex)
        {
            // A missing or unreadable model directory is not fatal: the built-in strategies still work.
            logger.LogError(ex, "Error discovering bot types in {ModelDirectory}", directory);
        }

        return botTypes;
    }
}
