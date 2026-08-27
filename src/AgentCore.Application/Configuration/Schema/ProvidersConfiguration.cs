using System.Text.Json.Serialization;
using AgentCore.Application.Knowledge;

namespace AgentCore.Application.Configuration.Schema;

/// <summary>
/// One large language model, and the name a <see cref="ModelReference"/> points at.
/// </summary>
public sealed record LlmProviderConfiguration
{
    /// <summary>Gets the vendor, such as <c>openai</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Gets the model name the vendor knows.</summary>
    public required string Model { get; init; }

    /// <summary>Gets the name this entry answers to, such as <c>reply</c> or <c>fill</c>.</summary>
    public required string As { get; init; }

    /// <summary>
    /// Gets how hard a reasoning model thinks before it answers, or <see langword="null"/> to send
    /// nothing and let the vendor decide.
    /// </summary>
    public string? ReasoningEffort { get; init; }
}

/// <summary>
/// A provider that names one vendor and nothing else, such as speech or telephony.
/// </summary>
public sealed record VendorProviderConfiguration
{
    /// <summary>Gets the vendor, such as <c>telnyx-relay</c>.</summary>
    public required string Kind { get; init; }
}

/// <summary>
/// The two speech roles: who turns sound into text, and who turns text back into sound.
/// </summary>
public sealed record SpeechProviderConfiguration
{
    /// <summary>Gets the recognition vendor: what the caller said, turned into text.</summary>
    public required VendorProviderConfiguration Stt { get; init; }

    /// <summary>Gets the synthesis vendor: text, turned into what the caller hears.</summary>
    public required VendorProviderConfiguration Tts { get; init; }
}

/// <summary>
/// The vendor that carries the call, and the limits of the socket it opens.
/// </summary>
public sealed record CallProviderConfiguration
{
    /// <summary>Gets the vendor that carries the call, such as <c>telnyx-relay</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Gets how long the socket waits with no inbound frame before ending the call, or null for the adapter's default.</summary>
    public int? IdleTimeoutSeconds { get; init; }

    /// <summary>Gets how long teardown gives a stuck task before moving on, or null for the adapter's default.</summary>
    public int? CloseTimeoutSeconds { get; init; }

    /// <summary>Gets the largest inbound frame the socket accepts, in bytes, or null for the adapter's default.</summary>
    public int? MaxFrameBytes { get; init; }
}

/// <summary>
/// The embedding provider: the vendor that turns a query into a vector, and the model it uses.
/// </summary>
public sealed record EmbeddingProviderConfiguration
{
    /// <summary>Gets the vendor, such as <c>openai</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Gets the model name the vendor knows, such as <c>text-embedding-3-small</c>.</summary>
    public required string Model { get; init; }

    /// <summary>
    /// Gets the vector width to ask the vendor for, or <see langword="null"/> for the model's own.
    /// It must match the width the knowledge collection was built with.
    /// </summary>
    public int? Dimensions { get; init; }
}

/// <summary>How a card's payload is named in the collection this deployment reads.</summary>
public sealed record KnowledgeFieldsConfiguration
{
    /// <summary>The id field used when the document names none.</summary>
    public const string DefaultId = "card_id";

    /// <summary>The body field used when the document names none.</summary>
    public const string DefaultBody = "body";

    /// <summary>The full-text field used when the document names none.</summary>
    public const string DefaultLexical = "text";

    /// <summary>The citation source field used when the document names none.</summary>
    public const string DefaultSource = "source.ref";

    /// <summary>The citation locator field used when the document names none.</summary>
    public const string DefaultLocator = "source.locator";

    /// <summary>The trust-rank field used when the document names none.</summary>
    public const string DefaultAuthority = "authority";

    /// <summary>Gets the field holding the card id, or null when the collection carries none.</summary>
    public string? Id { get; init; } = DefaultId;

    /// <summary>Gets the field holding what the model reads.</summary>
    public string Body { get; init; } = DefaultBody;

    /// <summary>Gets the full-text-indexed field the required-term leg matches on.</summary>
    public string? Lexical { get; init; } = DefaultLexical;

    /// <summary>Gets the field holding the citation's source label. Empty output omits the citation.</summary>
    public string? Source { get; init; } = DefaultSource;

    /// <summary>Gets the field holding where in that source the card sits.</summary>
    public string? Locator { get; init; } = DefaultLocator;

    /// <summary>Gets the field holding the trust rank. Read by the audit record only.</summary>
    public string? Authority { get; init; } = DefaultAuthority;
}

/// <summary>How an open <c>KnowledgeScope</c>'s facet keys become payload paths.</summary>
public sealed record KnowledgeScopeConfiguration
{
    /// <summary>The template used when the document names none.</summary>
    public const string DefaultTemplate = "facets.{key}";

    /// <summary>
    /// Gets the payload path each facet key becomes, with <c>{key}</c> standing for the key.
    /// </summary>
    public string Template { get; init; } = DefaultTemplate;
}

/// <summary>How a card id becomes something Qdrant can fetch.</summary>
public enum KnowledgeLinkLookup
{
    /// <summary>The point key is <c>uuid5(namespace, prefix + id)</c>. One fetch by key.</summary>
    Uuid5,

    /// <summary>The point key is the card id itself. One fetch by key.</summary>
    Direct,

    /// <summary>The point key is unrelated to the id. One scroll, matching on the id field.</summary>
    Filter,
}

/// <summary>How a card's links to other cards are read and followed.</summary>
/// <remarks>
/// The whole block is opt-in: a document without <c>links:</c> never expands. On a configured
/// block, a collection carrying nothing at <see cref="Field"/> is still inert.
/// </remarks>
public sealed record KnowledgeLinksConfiguration
{
    /// <summary>The links field used when the document names none.</summary>
    public const string DefaultField = "see_also";

    /// <summary>The namespace used when the document names none.</summary>
    public const string DefaultNamespace = "url";

    /// <summary>The prefix used when the document names none.</summary>
    public const string DefaultPrefix = "kb:";

    /// <summary>Gets the payload field holding the ids of the cards this card links to.</summary>
    public string Field { get; init; } = DefaultField;

    /// <summary>Gets how a linked id becomes a point to fetch. <c>filter</c> works on any collection.</summary>
    public KnowledgeLinkLookup Lookup { get; init; } = KnowledgeLinkLookup.Filter;

    /// <summary>Gets the uuid5 namespace: <c>url</c>, <c>dns</c>, <c>oid</c>, <c>x500</c>, or a GUID.</summary>
    public string Namespace { get; init; } = DefaultNamespace;

    /// <summary>Gets what the ingester puts in front of the id before hashing. May be empty.</summary>
    public string Prefix { get; init; } = DefaultPrefix;
}

/// <summary>
/// The knowledge provider: the adapter that answers <c>IKnowledgeRetrievalPort</c>, and what it reads.
/// </summary>
public sealed record KnowledgeProviderConfiguration
{
    /// <summary>The adapter used when the document names none.</summary>
    public const string DefaultKind = "qdrant";

    /// <summary>The collection used when the document names none.</summary>
    public const string DefaultCollection = "kb";

    /// <summary>The analyzer used when the document names none.</summary>
    public const string DefaultAnalyzer = IdentifierCodeAnalyzer.AnalyzerName;

    /// <summary>The score floor used when the document sets none.</summary>
    public const double DefaultScoreFloor = 0.25;

    /// <summary>Gets the adapter that answers <c>IKnowledgeRetrievalPort</c>, such as <c>qdrant</c>.</summary>
    public string Kind { get; init; } = DefaultKind;

    /// <summary>Gets the Qdrant endpoint, such as <c>https://qdrant.example.com:6334</c>.</summary>
    public string? Endpoint { get; init; }

    /// <summary>Gets the collection to read.</summary>
    public string Collection { get; init; } = DefaultCollection;

    /// <summary>
    /// Gets the named vector every query searches, or <see langword="null"/> for the collection's
    /// single anonymous vector — the default shape most Qdrant tooling creates.
    /// </summary>
    public string? Vector { get; init; }

    /// <summary>Gets how a card's payload is named.</summary>
    public KnowledgeFieldsConfiguration Fields { get; init; } = new();

    /// <summary>Gets how facet keys become payload paths.</summary>
    public KnowledgeScopeConfiguration Scope { get; init; } = new();

    /// <summary>Gets how links between cards are read and followed, or <see langword="null"/> for no link expansion.</summary>
    public KnowledgeLinksConfiguration? Links { get; init; }

    /// <summary>Gets the <c>IKnowledgeQueryAnalyzer</c> name that picks required terms.</summary>
    public string Analyzer { get; init; } = DefaultAnalyzer;

    /// <summary>
    /// Gets the <c>IKnowledgePointMapper</c> name that turns a point into a card, or
    /// <see langword="null"/> for the <c>fields:</c> mapping above.
    /// </summary>

    public string? Mapper { get; init; }

    /// <summary>Gets the smallest fused score a card may carry, in the range 0 to 1.</summary>
    public double ScoreFloor { get; init; } = DefaultScoreFloor;
}

/// <summary>
/// The telemetry provider: where the signals go, and what is listened to on the way.
/// </summary>
public sealed record TelemetryProviderConfiguration
{
    /// <summary>The service name used when the document sets none.</summary>
    public const string DefaultServiceName = "agentcore";

    /// <summary>
    /// The metric export interval used when the document sets none, in milliseconds.
    /// </summary>
    public const int DefaultExportIntervalMilliseconds = 60_000;

    /// <summary>The lowest interval T61 allows, in milliseconds.</summary>
    public const int MinimumExportIntervalMilliseconds = 60_000;

    /// <summary>The extra <c>ActivitySource</c> names listened to when the document lists none.</summary>
    public static readonly IReadOnlyList<string> DefaultSources =
        ["Microsoft.AspNetCore", "System.Net.Http", "Experimental.Microsoft.Extensions.AI", "Experimental.Microsoft.Agents.AI"];

    /// <summary>The extra <c>Meter</c> names listened to when the document lists none.</summary>
    public static readonly IReadOnlyList<string> DefaultMeters =
        [
            "Microsoft.AspNetCore.Hosting",
            "Microsoft.AspNetCore.Server.Kestrel",
            "System.Net.Http",
            "Experimental.Microsoft.Extensions.AI",
            "Experimental.Microsoft.Agents.AI",
        ];

    /// <summary>Gets the vendor, such as <c>grafana</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Gets the collector URL, or <see langword="null"/> to read it from the environment.</summary>
    public string? Endpoint { get; init; }

    /// <summary>Gets the <c>service.name</c> every exported signal carries.</summary>
    public string ServiceName { get; init; } = DefaultServiceName;

    /// <summary>Gets how often metrics are sent, in milliseconds, never below the T61 floor.</summary>
    [JsonPropertyName("exportIntervalMs")]
    public int ExportIntervalMilliseconds
    {
        get => field;
        init => field = Math.Max(value, MinimumExportIntervalMilliseconds);
    } = DefaultExportIntervalMilliseconds;

    /// <summary>Gets the extra <c>ActivitySource</c> names listened to, beside the one AgentCore owns.</summary>
    public IReadOnlyList<string> Sources { get; init; } = DefaultSources;

    /// <summary>Gets the extra <c>Meter</c> names listened to, beside the one AgentCore owns.</summary>
    public IReadOnlyList<string> Meters { get; init; } = DefaultMeters;
}

/// <summary>
/// The <c>providers:</c> section. It configures adapters and never changes agent shape.
/// </summary>
public sealed record ProvidersConfiguration
{
    /// <summary>Gets the language models, in document order.</summary>
    public IReadOnlyList<LlmProviderConfiguration> Llm { get; init; } = [];

    /// <summary>Gets the vendor that carries the call and owns its inbound route, or <see langword="null"/>.</summary>
    public CallProviderConfiguration? Call { get; init; }

    /// <summary>Gets the two speech roles, or <see langword="null"/>.</summary>
    public SpeechProviderConfiguration? Speech { get; init; }

    /// <summary>Gets the telephony provider, or <see langword="null"/>.</summary>
    public VendorProviderConfiguration? Telephony { get; init; }

    /// <summary>Gets the moderation provider, or <see langword="null"/>.</summary>
    public VendorProviderConfiguration? Moderation { get; init; }

    /// <summary>Gets the embedding provider, or <see langword="null"/>.</summary>
    public EmbeddingProviderConfiguration? Embeddings { get; init; }

    /// <summary>Gets the knowledge provider, or <see langword="null"/>.</summary>
    public KnowledgeProviderConfiguration? Knowledge { get; init; }

    /// <summary>Gets the telemetry provider, or <see langword="null"/>.</summary>
    public TelemetryProviderConfiguration? Telemetry { get; init; }

    /// <summary>Gets the audit sink provider, or <see langword="null"/> for the in-process default.</summary>
    public VendorProviderConfiguration? Audit { get; init; }

    /// <summary>Gets the transcript store provider, or <see langword="null"/> for the in-process default.</summary>
    public VendorProviderConfiguration? Transcript { get; init; }
}
