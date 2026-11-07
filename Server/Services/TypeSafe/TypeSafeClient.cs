using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Imperial2030.Server.Services.TypeSafe;

/// <summary>One Choice question: what to decide, and the options it may answer with.</summary>
/// <param name="Instructions">
/// What the model should decide. A plain string, or an object holding the question in one field and the
/// data it refers to in others.
/// </param>
/// <param name="Criteria">
/// Option name -> what that option means. The names come back verbatim in <see cref="ChoiceAnswer.Choice"/>,
/// so the caller keeps its own map from name to whatever the option stands for.
/// </param>
public sealed record ChoiceQuestion(object Instructions, IReadOnlyDictionary<string, object?> Criteria);

/// <summary>The answer to a <see cref="ChoiceQuestion"/>: the picked option and how the probability was spread.</summary>
public sealed record ChoiceAnswer(string Choice, IReadOnlyDictionary<string, double> Probabilities, double Confidence);

/// <summary>
/// One yes/no question. <paramref name="WhenTrue"/> and <paramref name="WhenFalse"/> say what each answer
/// would mean - optional, but a bare question usually leaves the model guessing which way "yes" points.
/// </summary>
public sealed record NoulQuestion(object Instructions, object? WhenTrue = null, object? WhenFalse = null);

/// <summary>
/// The answer to a <see cref="NoulQuestion"/>: the probability that the answer is yes. A Noul has no
/// separate confidence - 0.5 means the two answers are equally likely, not "medium yes" - so
/// <see cref="Decisiveness"/> is how far from the coin flip it landed, on the same 0..1 scale a Choice
/// reports confidence on.
/// </summary>
public sealed record NoulAnswer(double Probability)
{
    public bool IsYes => Probability >= 0.5;
    public double Decisiveness => Math.Abs(2 * Probability - 1);
}

/// <summary>
/// The TypeSafe System One endpoint: a state plus typed questions in, typed answers out
/// (https://docs.typesafe.ai/api). Used by the Jev bot layer to second-guess a rondel move.
///
/// Every failure - no API key, a refused or malformed response, a timeout, a DNS outage - returns null
/// rather than throwing. A bot turn must not die because an outside service hiccuped; the caller keeps
/// whatever decision it had already made on its own.
/// </summary>
public sealed class TypeSafeClient
{
    public const string Endpoint = "https://api.typesafe.ai/v1/systemone";

    /// <summary>TypeSafe's flagship System One model, tracking whatever the current version is.</summary>
    public const string Model = "jev-latest";

    /// <summary>
    /// Where the key is read from: user-secrets in development, the TypeSafe__ApiKey environment
    /// variable in production - the same arrangement as Jwt__Key. Never a checked-in file.
    /// </summary>
    public const string ApiKeyConfigurationKey = "TypeSafe:ApiKey";

    public const string HttpClientName = "TypeSafe";

    /// <summary>
    /// How concentrated a Choice distribution has to be before a caller acts on the answer. Lives here
    /// because this is where the service's configuration is read; what it means is the caller's business
    /// (see JevRlBotStrategy). Set it with TypeSafe:MinimumConfidence to retune a deployed server.
    /// </summary>
    public const string MinimumConfidenceConfigurationKey = "TypeSafe:MinimumConfidence";

    /// <summary>
    /// Whether a caller should only consult the service where its own move already looks questionable.
    /// Set TypeSafe:AskOnEveryMove to true to ask about every decision instead (what the measurement
    /// runs compare against).
    /// </summary>
    public const string AskOnEveryMoveConfigurationKey = "TypeSafe:AskOnEveryMove";

    /// <summary>
    /// Which decisions to consult the service about, as a comma-separated list of question ids
    /// (TypeSafe:Decisions), or "all". Unset means <see cref="DefaultDecisions"/>.
    /// </summary>
    public const string DecisionsConfigurationKey = "TypeSafe:Decisions";

    /// <summary>
    /// The decisions a second opinion is asked about by default. Measured, not chosen: over 90 games this
    /// set came out +5.9 points a game against the model it wraps (95% interval +/-8.9) at ~9 calls a
    /// game, while asking about every decision came out -1.8 (+/-7.4) at ~31-36 calls. Neither interval
    /// excludes zero, so this is the cheaper of two configurations that measure the same, not a verdict
    /// on the two left out - they are implemented and switchable with TypeSafe:Decisions = "all".
    /// (An earlier run had them 13 points a game worse; that was a question missing the board, since fixed.)
    /// </summary>
    public static readonly string[] DefaultDecisions =
    [
        "rondel_space", "bond_to_buy", "factory_city", "unit_to_buy", "fight_or_retreat", "destroy_the_factory"
    ];

    /// <summary>
    /// The default gate. Measured, not guessed: over 24 head-to-head games against the model it wraps,
    /// taking every answer beat gating at 0.5 - see jev.info.txt for the runs.
    /// </summary>
    public const double DefaultMinimumConfidence = 0.5;

    /// <summary>
    /// A bot move waits on this call, and a human is watching the board while it does, so it is kept
    /// short: past it the bot plays its own decision instead of waiting longer.
    /// </summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Func<HttpClient> _clientFactory;
    private readonly string? _apiKey;
    private readonly ILogger _logger;

    public TypeSafeClient(IConfiguration? configuration, IHttpClientFactory? httpClientFactory = null, ILogger<TypeSafeClient>? logger = null)
        : this(configuration?[ApiKeyConfigurationKey],
               httpClientFactory != null ? () => httpClientFactory.CreateClient(HttpClientName) : () => new HttpClient(),
               logger,
               configuration?.GetValue<double?>(MinimumConfidenceConfigurationKey),
               configuration?.GetValue<bool?>(AskOnEveryMoveConfigurationKey),
               configuration?[DecisionsConfigurationKey])
    {
    }

    /// <summary>Takes the transport directly, so tests can answer (or refuse) without a network.</summary>
    internal TypeSafeClient(string? apiKey, Func<HttpClient> clientFactory, ILogger? logger = null,
                            double? minimumConfidence = null, bool? askOnEveryMove = null, string? decisions = null)
    {
        AskOnEveryMove = askOnEveryMove ?? false;
        _decisions = string.IsNullOrWhiteSpace(decisions)
            ? DefaultDecisions.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : decisions.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)
                ? null
                : decisions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                           .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        _clientFactory = clientFactory;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        MinimumConfidence = minimumConfidence ?? DefaultMinimumConfidence;
    }

    /// <summary>The configured gate, or <see cref="DefaultMinimumConfidence"/>.</summary>
    public double MinimumConfidence { get; }

    /// <summary>Whether callers should ask about every decision rather than only questionable ones.</summary>
    public bool AskOnEveryMove { get; }

    private readonly HashSet<string>? _decisions;

    /// <summary>Whether this decision is one of the ones configured to be asked about (null means all).</summary>
    public bool Asks(string questionId) => _decisions == null || _decisions.Contains(questionId);

    /// <summary>
    /// Whether a key was configured at all. False means every ask returns null, so a caller can skip
    /// building the question - and the bot catalogue can stop offering bots that depend on it.
    /// </summary>
    public bool IsConfigured => _apiKey != null;

    /// <summary>
    /// Asks one Choice question about <paramref name="state"/>. Returns null when the answer cannot be
    /// had or cannot be trusted to be the answer to this question (wrong type, no answer at all).
    /// </summary>
    public async Task<ChoiceAnswer?> AskChoiceAsync(object state, string questionId, ChoiceQuestion question, CancellationToken cancellationToken = default)
    {
        var body = await PostAsync(state, questionId,
            new { type = "choice", instructions = question.Instructions, criteria = question.Criteria }, cancellationToken);
        if (body == null) return null;

        try
        {
            return ParseChoice(body, questionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TypeSafe sent an answer to {QuestionId} that could not be read", questionId);
            return null;
        }
    }

    /// <summary>
    /// The one request both primitives make: the state and a single typed question in, the raw response
    /// body out, or null when there was no usable answer. Nothing here throws - a bot turn must not die
    /// because an outside service hiccuped; the caller keeps whatever decision it had already made.
    /// </summary>
    private async Task<string?> PostAsync(object state, string questionId, object question, CancellationToken cancellationToken)
    {
        if (_apiKey == null) return null;

        try
        {
            var payload = new
            {
                state,
                model = Model,
                questions = new Dictionary<string, object> { [questionId] = question }
            };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(RequestTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, SerializerOptions), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            var client = _clientFactory();
            using var response = await client.SendAsync(request, cts.Token);
            string body = await response.Content.ReadAsStringAsync(cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                // The body names the offending field on a 422 and the limit on a 429, so it is worth
                // keeping - but it is an outside service's text, so only a bounded prefix of it.
                _logger.LogWarning("TypeSafe refused the {QuestionId} question: {Status} {Body}",
                                   questionId, (int)response.StatusCode, Truncate(body));
                return null;
            }

            return body;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TypeSafe could not answer the {QuestionId} question", questionId);
            return null;
        }
    }

    /// <summary>The answer to the question that was asked, of the type it was asked as.</summary>
    private bool TryReadAnswer(JsonDocument document, string questionId, string expectedType, out JsonElement answer)
    {
        answer = default;

        if (!document.RootElement.TryGetProperty("answers", out var answers) || !answers.TryGetProperty(questionId, out answer))
        {
            _logger.LogWarning("TypeSafe returned no answer for {QuestionId}", questionId);
            return false;
        }

        // Another primitive's answer here would mean the question was not the one that was asked.
        if (!answer.TryGetProperty("type", out var type) || type.GetString() != expectedType)
        {
            _logger.LogWarning("TypeSafe answered {QuestionId} with something other than a {Expected}", questionId, expectedType);
            return false;
        }

        return true;
    }

    /// <summary>
    /// What the call cost and which version answered. Logged because this is the only place the spend is
    /// visible from our side: the service's own usage page is the other party's ledger, and it does not
    /// say which of our decisions a token went to.
    /// </summary>
    private void LogUsage(JsonDocument document, string questionId, string answer, double confidence)
    {
        int inputTokens = 0, outputTokens = 0;
        if (document.RootElement.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("input_tokens", out var input)) input.TryGetInt32(out inputTokens);
            if (usage.TryGetProperty("output_tokens", out var output)) output.TryGetInt32(out outputTokens);
        }
        string version = document.RootElement.TryGetProperty("model", out var model) ? model.GetString() ?? Model : Model;

        _logger.LogInformation("{Model} answered {QuestionId} with \"{Choice}\" at {Confidence:0.00} confidence, {InputTokens} in + {OutputTokens} out tokens",
                               version, questionId, answer, confidence, inputTokens, outputTokens);
    }

    /// <summary>
    /// Asks one yes/no question about <paramref name="state"/>. Null when the answer cannot be had or is
    /// not the answer to this question, exactly as <see cref="AskChoiceAsync"/>.
    /// </summary>
    public async Task<NoulAnswer?> AskNoulAsync(object state, string questionId, NoulQuestion question, CancellationToken cancellationToken = default)
    {
        var criteria = question.WhenTrue == null && question.WhenFalse == null
            ? null
            : new Dictionary<string, object?> { ["true"] = question.WhenTrue, ["false"] = question.WhenFalse };

        var body = await PostAsync(state, questionId, new { type = "noul", instructions = question.Instructions, criteria }, cancellationToken);
        if (body == null) return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            if (!TryReadAnswer(document, questionId, "noul", out var answer)) return null;
            if (!answer.TryGetProperty("noul", out var value) || !value.TryGetDouble(out double probability)) return null;

            LogUsage(document, questionId, $"{probability:0.00}", probability);
            return new NoulAnswer(probability);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TypeSafe sent an answer to {QuestionId} that could not be read", questionId);
            return null;
        }
    }

    private ChoiceAnswer? ParseChoice(string body, string questionId)
    {
        using var document = JsonDocument.Parse(body);

        if (!TryReadAnswer(document, questionId, "choice", out var answer)) return null;
        if (!answer.TryGetProperty("choice", out var choice) || choice.GetString() is not { } chosen) return null;

        var probabilities = new Dictionary<string, double>();
        if (answer.TryGetProperty("probabilities", out var distribution) && distribution.ValueKind == JsonValueKind.Object)
        {
            foreach (var option in distribution.EnumerateObject())
            {
                if (option.Value.TryGetDouble(out double probability)) probabilities[option.Name] = probability;
            }
        }

        double confidence = answer.TryGetProperty("confidence", out var element) && element.TryGetDouble(out double value) ? value : 0;
        LogUsage(document, questionId, chosen, confidence);

        return new ChoiceAnswer(chosen, probabilities, confidence);
    }

    private const int LoggedBodyLimit = 500;

    private static string Truncate(string body)
        => body.Length <= LoggedBodyLimit ? body : body[..LoggedBodyLimit] + "...";
}
