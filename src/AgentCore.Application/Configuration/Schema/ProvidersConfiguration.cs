using System.Text.Json.Serialization;

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

/// <summary>
/// The knowledge provider: the adapter that answers <c>IKnowledgeRetrievalPort</c>, and what it reads.
/// </summary>
public sealed record KnowledgeProviderConfiguration
{
    /// <summary>The adapter used when the document names none.</summary>
    public const string DefaultKind = "qdrant";

    /// <summary>The collection used when the document names none.</summary>
    public const string DefaultCollection = "kb";

    /// <summary>Gets the adapter that answers <c>IKnowledgeRetrievalPort</c>, such as <c>qdrant</c>.</summary>
    public string Kind { get; init; } = DefaultKind;

    /// <summary>Gets the Qdrant endpoint, such as <c>https://qdrant.example.com:6334</c>.</summary>
    public string? Endpoint { get; init; }

    /// <summary>Gets the collection to read.</summary>
    /// <remarks>
    /// Always the alias, never a concrete collection. <c>kb sync --rebuild</c> swaps the alias and
    /// AgentCore never notices.
    /// </remarks>
    public string Collection { get; init; } = DefaultCollection;
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
