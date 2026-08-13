using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Evaluation;
using AgentCore.Application.Ports;
using AgentCore.Application.Secrets;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace AgentCore.Infrastructure.Evaluation.OpenAiModeration;

/// <summary>
/// Checks one piece of text against the OpenAI moderations endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Decision D13 makes <see cref="IEvaluator"/> the moderation port, so this class is the port rather
/// than a factory for one. It posts to <see cref="ModerationPath"/> with
/// <see cref="ModerationModel"/>, and it produces one <see cref="BooleanMetric"/> named
/// <see cref="ContentSafetyMetricName"/> with one <see cref="ModerationVerdict"/> attached to it. The
/// caller reads <see cref="ModerationVerdict.TryRead"/> and parses no string.
/// </para>
/// <para>
/// <b>This evaluator moderates whatever text it is given, and the caller decides which text that
/// is.</b> The text arrives through <c>modelResponse.Text</c> because that is the shape
/// <see cref="IEvaluator"/> fixes, and the name of that parameter is not a statement about whose
/// words they are. The host checks what the caller said, before the model runs, so the agent can
/// refuse rather than answer and retract.
/// </para>
/// <para>
/// <b>It never throws for a vendor problem.</b> D9 says a judge must never block a turn, and
/// moderation runs on every turn. A 5xx, a 429, a timeout, a body that is not JSON, and an empty
/// <c>results</c> array all give a metric with no value, <see cref="EvaluationRating.Inconclusive"/>,
/// <c>failed: false</c>, and one <see cref="EvaluationDiagnostic"/> of severity
/// <see cref="EvaluationDiagnosticSeverity.Error"/>. No verdict is attached, so the presence of a
/// verdict is what says the endpoint answered. A cancel by the caller is not a vendor problem, and it
/// still throws.
/// </para>
/// <para>
/// The value of the metric is <see langword="true"/> when the endpoint flagged nothing. That is the
/// <c>FaultCodeEvaluator</c> convention, where <see langword="true"/> means the reply is right, and
/// the name of this metric is a safety name rather than a harm name.
/// </para>
/// <para>
/// No key appears in this file. The chain is asked for
/// <see cref="Llm.OpenAI.OpenAiChatClientAdapter.ApiKeySecretName"/>, the
/// <see cref="Llm.OpenAI.OpenAiChatClientAdapter.ApiKeyVariableName"/> variable answers when the
/// chain holds nothing, and <see cref="OpenAiModerationAuthHandler"/> writes it. D13 gives one key to
/// all four OpenAI calls, so this class declares no name of its own. The read happens once, in
/// <see cref="CreateAsync"/>, while the host starts, and it opens no socket.
/// </para>
/// </remarks>
public sealed class OpenAiModerationEvaluator : IEvaluator
{
    /// <summary>The name this evaluator is registered under, beside <c>fault_code</c>.</summary>
    /// <remarks>
    /// It is the name <see cref="PromptModerator"/> looks the moderator up by, and never a second
    /// name for the same thing. The turn loop finds no moderator at all if the two ever drift, and
    /// every turn would then reach the model unchecked with nothing reporting it, so this constant
    /// forwards rather than repeats. Application owns the name because the reader lives there;
    /// Infrastructure already references Application under D3.
    /// </remarks>
    public const string EvaluatorName = PromptModerator.ModerationEvaluatorName;

    /// <summary>The name of the one metric this evaluator produces.</summary>
    public const string ContentSafetyMetricName = "Content Safety";

    /// <summary>The moderation model D13 names. Its price is nothing, at any volume.</summary>
    public const string ModerationModel = "omni-moderation-latest";

    /// <summary>The route one check posts to.</summary>
    public const string ModerationPath = "/v1/moderations";

    /// <summary>The name this evaluator opens its client under, on the pipeline of the host.</summary>
    /// <remarks>
    /// The pipeline serves any name and gives each one the same defaults, so this name is chosen
    /// here, beside the vendor it belongs to, and no host registers it in advance.
    /// </remarks>
    public const string HttpClientName = "agentcore.openai.moderation";

    /// <summary>The host every moderation request goes to.</summary>
    public static readonly Uri ApiEndpoint = new("https://api.openai.com", UriKind.Absolute);

    /// <summary>The deadline of one check, over every attempt the pipeline makes.</summary>
    /// <remarks>
    /// A caller is waiting on the telephone while this runs. The shipped default of 100 seconds is
    /// longer than the call would survive.
    /// </remarks>
    public static readonly TimeSpan ModerationDeadline = TimeSpan.FromSeconds(10);

    /// <summary>The thirteen category names D13 counts, in the order the endpoint writes them.</summary>
    /// <remarks>
    /// Nothing in the run-time path indexes by this list. The reader enumerates whatever the endpoint
    /// answered, so a fourteenth category reaches the audit chain with no code change. The list is
    /// here so the vocabulary of the vendor is written down once, with its slashes and hyphens.
    /// </remarks>
    public static readonly IReadOnlyList<string> Categories =
    [
        "harassment",
        "harassment/threatening",
        "hate",
        "hate/threatening",
        "illicit",
        "illicit/violent",
        "self-harm",
        "self-harm/instructions",
        "self-harm/intent",
        "sexual",
        "sexual/minors",
        "violence",
        "violence/graphic",
    ];

    /// <summary>The metadata key that records which model answered.</summary>
    private const string ModelMetadataKey = "model";

    /// <summary>The reason every inconclusive metric of this evaluator carries.</summary>
    private const string UncheckedReason = "the moderation endpoint did not answer, so the text is unchecked.";

    /// <summary>The score format one flagged category is recorded in.</summary>
    private const string ScoreFormat = "0.####";

    private readonly HttpClient _client;

    /// <summary>Opens the evaluator over one client.</summary>
    /// <param name="client">
    /// The client. Its <c>BaseAddress</c> is <see cref="ApiEndpoint"/>, and its handler chain carries
    /// the key: <see cref="OpenAiModerationAuthHandler"/> writes the bearer token, and this class
    /// writes none.
    /// </param>
    /// <remarks>
    /// This is internal for the reason <c>ZillizCollection.Deadline</c> is internal. An
    /// <see cref="HttpClient"/> in a public signature would put the transport into the D15 promise. A
    /// host builds through <see cref="CreateAsync"/>, and a test reaches this through
    /// <c>InternalsVisibleTo</c>.
    /// </remarks>
    internal OpenAiModerationEvaluator(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        _client = client;
    }

    /// <summary>Gets the name of the one metric this evaluator produces.</summary>
    public IReadOnlyCollection<string> EvaluationMetricNames => [ContentSafetyMetricName];

    /// <summary>Gets the deadline of one check, which <see cref="CreateAsync"/> set on the client.</summary>
    /// <remarks>
    /// The build sets the deadline and this class sends on the client, so a test reads it back here.
    /// It is internal, so it adds nothing to the public surface.
    /// </remarks>
    internal TimeSpan Deadline => _client.Timeout;

    /// <summary>Builds the evaluator, over the pipeline the host built.</summary>
    /// <param name="handlers">
    /// The outbound HTTP pipeline. This evaluator asks it for <see cref="HttpClientName"/>.
    /// </param>
    /// <param name="secrets">The chain the key resolves through, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the key read.</param>
    /// <returns>The evaluator.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="handlers"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecretResolutionException">Neither the chain nor the environment holds a key.</exception>
    /// <remarks>
    /// <para>
    /// This runs once, while the host starts. It opens no socket: the first check is the first
    /// request, so a host with no route to <see cref="ApiEndpoint"/> still starts.
    /// </para>
    /// <para>
    /// <b>The pipeline is required rather than optional.</b> A handler built here instead would send
    /// with no retry and no rate limit answer, and nothing would say so.
    /// </para>
    /// <para>
    /// The evaluator holds no per-call state, so one instance serves every turn of every call.
    /// </para>
    /// </remarks>
    public static async ValueTask<OpenAiModerationEvaluator> CreateAsync(
        IHttpMessageHandlerFactory handlers,
        ISecretResolverPort? secrets = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handlers);

        var apiKey = await ResolveKeyAsync(secrets, cancellationToken).ConfigureAwait(false);

        // The key is written onto the request one layer below this class, so no class that builds a
        // body or reads an answer holds a credential.
        var inner = handlers.CreateHandler(HttpClientName);

        // The pipeline owns the chain below this handler, and other clients send on the same chain,
        // so this client disposes nothing.
        HttpClient client = new(new OpenAiModerationAuthHandler(apiKey) { InnerHandler = inner }, disposeHandler: false)
        {
            BaseAddress = ApiEndpoint,
            Timeout = ModerationDeadline,
        };

        return new OpenAiModerationEvaluator(client);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The text is <c>modelResponse.Text</c>, and the caller decides which text it puts there. The
    /// <paramref name="messages"/>, <paramref name="chatConfiguration"/>, and
    /// <paramref name="additionalContext"/> arguments are unread: this endpoint takes one string and
    /// no history.
    /// </para>
    /// <para>
    /// Text that is empty or blank sends no request and passes. The endpoint refuses an empty
    /// <c>input</c>, and a check that asks about nothing costs nothing.
    /// </para>
    /// <para>
    /// A vendor problem gives an inconclusive metric and no exception. A cancel by
    /// <paramref name="cancellationToken"/> still throws, because that is the decision of the caller
    /// and not a failure of the endpoint.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="modelResponse"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse modelResponse,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelResponse);
        cancellationToken.ThrowIfCancellationRequested();

        var text = modelResponse.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return new EvaluationResult(NothingToCheck());
        }

        string payload;
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, ModerationPath)
            {
                Content = new StringContent(Body(text), Encoding.UTF8, "application/json"),
            };

            // The key rides on the handler chain of this client, so no credential passes through here.
            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new EvaluationResult(Unchecked(
                    "the moderation endpoint answered HTTP "
                    + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)
                    + " " + response.StatusCode + "."));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The deadline ran out. A cancel by the caller leaves the token cancelled, so the filter
            // above is false and that exception travels on.
            return new EvaluationResult(Unchecked(
                "the moderation endpoint did not answer inside "
                + _client.Timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " seconds."));
        }
        catch (HttpRequestException error)
        {
            return new EvaluationResult(Unchecked(
                "the moderation endpoint could not be reached: " + error.Message));
        }

        return new EvaluationResult(Read(payload));
    }

    /// <summary>Writes the body the moderations route expects.</summary>
    /// <param name="text">The text to check.</param>
    /// <returns>The JSON body.</returns>
    /// <remarks>
    /// The route takes a string, an array of strings, or an array of content parts. D13 asks for one
    /// POST, and this host checks one piece of text, so it sends one string.
    /// </remarks>
    private static string Body(string text)
    {
        JsonObject body = new()
        {
            [ModelMetadataKey] = ModerationModel,
            ["input"] = text,
        };

        return body.ToJsonString();
    }

    /// <summary>Reads the answer of one check.</summary>
    /// <param name="payload">The body the endpoint answered.</param>
    /// <returns>The metric, which is inconclusive when the body cannot be read.</returns>
    private static BooleanMetric Read(string payload)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return Unchecked("the moderation endpoint answered a body that is not JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Unchecked("the moderation endpoint answered a body that is not a JSON object.");
            }

            if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            {
                return Unchecked("the moderation endpoint answered no results array.");
            }

            // One input gives one element. An empty array is no evidence about the text.
            if (results.GetArrayLength() == 0)
            {
                return Unchecked("the moderation endpoint answered an empty results array.");
            }

            var first = results[0];
            if (first.ValueKind != JsonValueKind.Object
                || !first.TryGetProperty("flagged", out var reported)
                || (reported.ValueKind != JsonValueKind.True && reported.ValueKind != JsonValueKind.False))
            {
                return Unchecked("the moderation endpoint answered a result with no flagged field.");
            }

            // The endpoint owns the verdict, at its own thresholds. Recomputing it from the flags
            // would drift the day OpenAI moves one.
            var flagged = reported.ValueKind == JsonValueKind.True;

            return Measure(first, flagged, Flags(first));
        }
    }

    /// <summary>Collects the names the endpoint flagged, in the order it returned them.</summary>
    /// <param name="result">The first element of <c>results</c>.</param>
    /// <returns>The names.</returns>
    /// <remarks>
    /// <c>illicit</c> and <c>illicit/violent</c> may be JSON <c>null</c>, so this tests for
    /// <see cref="JsonValueKind.True"/> and treats every other kind as not flagged. The taxonomy
    /// belongs to the endpoint and it is open, so a name this library never listed still travels.
    /// </remarks>
    private static List<string> Flags(JsonElement result)
    {
        List<string> names = [];
        if (!result.TryGetProperty("categories", out var categories) || categories.ValueKind != JsonValueKind.Object)
        {
            return names;
        }

        foreach (var category in categories.EnumerateObject())
        {
            if (category.Value.ValueKind == JsonValueKind.True)
            {
                names.Add(category.Name);
            }
        }

        return names;
    }

    /// <summary>Builds the metric one answered check produces.</summary>
    /// <param name="result">The first element of <c>results</c>, read for the scores.</param>
    /// <param name="flagged">What the endpoint answered.</param>
    /// <param name="names">The names the endpoint flagged, in its order.</param>
    /// <returns>The metric, with the verdict on it.</returns>
    private static BooleanMetric Measure(JsonElement result, bool flagged, List<string> names)
    {
        var reason = names.Count == 0 ? ModerationVerdict.NothingFlagged : string.Join(", ", names);

        // True means safe. FaultCodeEvaluator reads the same way, where true means the reply is right.
        BooleanMetric metric = new(ContentSafetyMetricName, value: !flagged, reason);
        metric.Interpretation = new EvaluationMetricInterpretation(
            flagged ? EvaluationRating.Unacceptable : EvaluationRating.Exceptional,
            failed: flagged,
            reason: reason);

        metric.AddOrUpdateContext(new ModerationVerdict(flagged, names));
        metric.AddOrUpdateMetadata(ModelMetadataKey, ModerationModel);

        // The scores of the flagged categories only. OpenAI does not present them as comparable
        // across categories, so nothing here aggregates them into one number.
        if (!result.TryGetProperty("category_scores", out var scores) || scores.ValueKind != JsonValueKind.Object)
        {
            return metric;
        }

        foreach (var name in names)
        {
            if (scores.TryGetProperty(name, out var score) && score.ValueKind == JsonValueKind.Number)
            {
                metric.AddOrUpdateMetadata(
                    name,
                    score.GetDouble().ToString(ScoreFormat, CultureInfo.InvariantCulture));
            }
        }

        return metric;
    }

    /// <summary>Builds the metric text with nothing in it produces.</summary>
    /// <returns>The metric, with a verdict that flags nothing.</returns>
    /// <remarks>
    /// A verdict is attached, because the answer is known: there is nothing to flag. That is a
    /// different fact from an endpoint that did not answer, which carries no verdict.
    /// </remarks>
    private static BooleanMetric NothingToCheck()
    {
        const string reason = "the text is empty, so nothing was posted.";

        BooleanMetric metric = new(ContentSafetyMetricName, value: true, reason);
        metric.Interpretation = new EvaluationMetricInterpretation(
            EvaluationRating.Exceptional,
            failed: false,
            reason: reason);

        metric.AddOrUpdateContext(new ModerationVerdict(flagged: false, []));

        return metric;
    }

    /// <summary>Builds the metric every failure of this evaluator produces.</summary>
    /// <param name="what">What went wrong, named for the diagnostic.</param>
    /// <returns>The metric, with no value and no verdict.</returns>
    /// <remarks>
    /// <c>failed</c> is false, because an endpoint that did not answer is no evidence about the text.
    /// A reader tells this apart from a clean check by <see cref="ModerationVerdict.TryRead"/>
    /// answering <see langword="false"/>.
    /// </remarks>
    private static BooleanMetric Unchecked(string what)
    {
        BooleanMetric metric = new(ContentSafetyMetricName, value: null);
        metric.AddDiagnostics(EvaluationDiagnostic.Error(what));
        metric.Interpretation = new EvaluationMetricInterpretation(
            EvaluationRating.Inconclusive,
            failed: false,
            reason: UncheckedReason);

        return metric;
    }

    /// <summary>Reads the OpenAI key through the chain and then the environment.</summary>
    /// <param name="secrets">The resolver chain, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The key.</returns>
    /// <exception cref="SecretResolutionException">Neither place holds one.</exception>
    private static async ValueTask<string> ResolveKeyAsync(
        ISecretResolverPort? secrets,
        CancellationToken cancellationToken)
    {
        string? key = null;
        if (secrets is not null)
        {
            key = await secrets
                .TryResolveAsync(Llm.OpenAI.OpenAiChatClientAdapter.ApiKeySecretName, cancellationToken)
                .ConfigureAwait(false);
        }

        key ??= Environment.GetEnvironmentVariable(Llm.OpenAI.OpenAiChatClientAdapter.ApiKeyVariableName);
        if (key is not { Length: > 0 })
        {
            throw new SecretResolutionException(
                "the OpenAI API key did not resolve, and the moderation evaluator checks every turn "
                + "against " + ModerationModel + ". Bind a resolver that holds '"
                + Llm.OpenAI.OpenAiChatClientAdapter.ApiKeySecretName + "', or set the "
                + Llm.OpenAI.OpenAiChatClientAdapter.ApiKeyVariableName
                + " variable. This evaluator holds no key of its own.");
        }

        return key;
    }
}
