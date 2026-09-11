using System.Text.Json.Serialization;
using AgentCore.Application.Knowledge;

namespace AgentCore.Application.Configuration.Schema;

/// <summary>
/// How a card's payload is named in the collection this deployment reads.
/// </summary>
/// <remarks>
/// AgentCore ships no field names. Every role starts unmapped, and stays unmapped until this
/// document names the payload path that fills it. A collection built by any ingester is therefore
/// expressible here, and no ingester's naming is privileged. An unmapped role is simply absent from
/// the card: it is never guessed, and never silently read off a name AgentCore chose.
/// </remarks>
public sealed record KnowledgeFieldsConfiguration
{
    /// <summary>
    /// Gets the field holding what the model reads, or <see langword="null"/> when this document
    /// names none. Required whenever <c>providers.knowledge.mapper</c> is absent, because the
    /// built-in mapping has nothing to put in the card without it.
    /// </summary>
    public string? Body { get; init; }

    /// <summary>Gets the field holding the card id, or null when the collection carries none.</summary>
    /// <remarks>The point's own key stands in for an unmapped id.</remarks>
    public string? Id { get; init; }

    /// <summary>Gets the full-text-indexed field the required-term leg matches on.</summary>
    /// <remarks>Unmapped, the search ranks by vector alone and the analyzer is never consulted.</remarks>
    public string? Lexical { get; init; }

    /// <summary>Gets the field holding the citation's source label.</summary>
    public string? Source { get; init; }

    /// <summary>Gets the field holding where in that source the card sits.</summary>
    public string? Locator { get; init; }

    /// <summary>Gets the field holding the trust rank. Read by the audit record only.</summary>
    public string? Authority { get; init; }
}

/// <summary>How an open <c>KnowledgeScope</c>'s facet keys become payload paths.</summary>
/// <remarks>
/// There is no default template, because a template is a claim about where one particular
/// collection keeps its facets. A deployment whose agents never scope needs none; one whose agents
/// do must say where to look.
/// </remarks>
public sealed record KnowledgeScopeConfiguration
{
    /// <summary>
    /// Gets the payload path each facet key becomes, with <c>{key}</c> standing for the key, or
    /// <see langword="null"/> when this document names none. Required whenever any agent declares
    /// <c>knowledge: { scoped: true }</c>.
    /// </summary>
    public string? Template { get; init; }

    /// <summary>Gets how a card marks itself reachable from every scope, or <see langword="null"/> for exact match only.</summary>
    public KnowledgeWildcardConfiguration? Wildcard { get; init; }

    /// <summary>Gets the state slots the turn's scope is built from. Each name becomes one facet key.</summary>
    public IReadOnlyList<string> FromState { get; init; } = [];

    /// <summary>Gets the facets a tool-mode agent may narrow its own search by. Empty offers none.</summary>
    public IReadOnlyList<KnowledgeFilterableFacetConfiguration> Filterable { get; init; } = [];
}

/// <summary>One facet the model may name in the search tool's <c>filters</c> argument.</summary>
public sealed record KnowledgeFilterableFacetConfiguration
{
    /// <summary>Gets the facet key, which <c>scope.template</c> turns into the payload path.</summary>
    public required string Key { get; init; }

    /// <summary>Gets what this facet means, in the words the model reads before it picks a value.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// Gets how the model finds an exact value for this facet, or <see langword="null"/> when the
    /// description alone is enough to pick one.
    /// </summary>
    public KnowledgeFacetResolveConfiguration? Resolve { get; init; }
}

/// <summary>
/// The lookup that turns what the person said into the one value a facet is stored under.
/// </summary>
/// <remarks>
/// A vocabulary of thousands cannot sit in a prompt. The model instead searches a small index — the
/// cards under one other facet — and copies the value off the card it finds. This block is printed
/// on the search tool, not run by the host: the model does the lookup itself.
/// </remarks>
public sealed record KnowledgeFacetResolveConfiguration
{
    /// <summary>Gets the facet and value that narrow the lookup search to the index cards.</summary>
    public required KnowledgeFacetValueConfiguration Via { get; init; }

    /// <summary>Gets what the model searches for, with placeholders in angle brackets for what the person said.</summary>
    public required string Query { get; init; }

    /// <summary>Gets where on the card the value is, in the words the model reads.</summary>
    public required string Read { get; init; }
}

/// <summary>One facet key and the value it must carry.</summary>
public sealed record KnowledgeFacetValueConfiguration
{
    /// <summary>Gets the facet key.</summary>
    public required string Key { get; init; }

    /// <summary>Gets the value the cards must carry.</summary>
    public required string Value { get; init; }
}

/// <summary>Which facets a card may opt out of, and the value it opts out with.</summary>
/// <remarks>
/// Named per facet, never global. A wildcard reaching an isolation facet such as a customer id
/// would serve one mis-tagged card to every caller, which is the failure the scope exists to stop.
/// </remarks>
public sealed record KnowledgeWildcardConfiguration
{
    /// <summary>Gets the payload value that satisfies any scope on a named facet.</summary>
    public required string Value { get; init; }

    /// <summary>Gets the facet keys the wildcard widens. Every other facet stays exact match.</summary>
    public required IReadOnlyList<string> Facets { get; init; }
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
    /// <summary>The uuid5 namespace used when the document names none.</summary>
    /// <remarks>
    /// This is RFC 4122's own URL namespace, not any ingester's choice. <c>uuid5</c> is undefined
    /// without a namespace, so unlike a field name it cannot be left unset.
    /// </remarks>
    public const string DefaultNamespace = "url";

    /// <summary>
    /// Gets the payload field holding the ids of the cards this card links to, or
    /// <see langword="null"/> when this document names none. Required inside a <c>links:</c> block.
    /// </summary>
    public string? Field { get; init; }

    /// <summary>Gets how a linked id becomes a point to fetch. <c>filter</c> works on any collection.</summary>
    public KnowledgeLinkLookup Lookup { get; init; } = KnowledgeLinkLookup.Filter;

    /// <summary>Gets the uuid5 namespace: <c>url</c>, <c>dns</c>, <c>oid</c>, <c>x500</c>, or a GUID.</summary>
    public string Namespace { get; init; } = DefaultNamespace;

    /// <summary>Gets what the ingester puts in front of the id before hashing. Empty by default.</summary>
    public string Prefix { get; init; } = string.Empty;
}

/// <summary>
/// The knowledge provider: the adapter that answers <c>IKnowledgeRetrievalPort</c>, and what it reads.
/// </summary>
public sealed record KnowledgeProviderConfiguration
{
    /// <summary>The analyzer used when the document names none: one that requires nothing.</summary>
    public const string DefaultAnalyzer = NoQueryAnalyzer.AnalyzerName;

    /// <summary>The citation wording used when the document names none.</summary>
    public const string DefaultCitation = SourceLocatorCitationFormatter.FormatterName;

    /// <summary>
    /// The score floor used when the document sets none: a cosine similarity, applied on the dense
    /// prefetch. On text-embedding-3-small, 0.25 let twelve of twenty cards through for a question
    /// the corpus did not answer; 0.35 let none through and kept every answered question's cards.
    /// 0 disables the floor.
    /// </summary>
    public const double DefaultScoreFloor = 0.35;

    /// <summary>Gets the adapter that answers <c>IKnowledgeRetrievalPort</c>, such as <c>qdrant</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Gets the Qdrant endpoint, such as <c>https://qdrant.example.com:6334</c>.</summary>
    public string? Endpoint { get; init; }

    /// <summary>Gets the collection to read.</summary>
    public required string Collection { get; init; }

    /// <summary>
    /// Gets the named vector every query searches, or <see langword="null"/> for the collection's
    /// single anonymous vector — the default shape most Qdrant tooling creates.
    /// </summary>
    public string? Vector { get; init; }

    /// <summary>
    /// Gets how a card's payload is named, or <see langword="null"/> when this document names
    /// nothing. Required whenever <see cref="Mapper"/> is absent.
    /// </summary>
    public KnowledgeFieldsConfiguration? Fields { get; init; }

    /// <summary>Gets how facet keys become payload paths.</summary>
    public KnowledgeScopeConfiguration Scope { get; init; } = new();

    /// <summary>
    /// Gets how the knowledge search asks the caller which machine they meant, or
    /// <see langword="null"/> when this document names none. Absent means neither the probe nor the
    /// clarification ever runs.
    /// </summary>
    public KnowledgeAmbiguityConfiguration? Ambiguity { get; init; }

    /// <summary>Gets how links between cards are read and followed, or <see langword="null"/> for no link expansion.</summary>
    public KnowledgeLinksConfiguration? Links { get; init; }

    /// <summary>Gets the <c>IKnowledgeQueryAnalyzer</c> name that picks required terms.</summary>
    public string Analyzer { get; init; } = DefaultAnalyzer;

    /// <summary>
    /// Gets the <c>IKnowledgeCitationFormatter</c> name that writes each card's source label.
    /// </summary>
    /// <remarks>Read only by agents that declare <c>knowledge: { citations: true }</c>.</remarks>
    public string Citation { get; init; } = DefaultCitation;

    /// <summary>
    /// Gets the <c>IKnowledgePointMapper</c> name that turns a point into a card, or
    /// <see langword="null"/> for the <c>fields:</c> mapping above.
    /// </summary>

    public string? Mapper { get; init; }

    /// <summary>
    /// Gets the cosine similarity a card must score strictly above, in the range 0 to 1. The store
    /// applies it on the dense prefetch, before any fusion; Qdrant drops a card scoring exactly it.
    /// 0 disables the floor.
    /// </summary>
    public double ScoreFloor { get; init; } = DefaultScoreFloor;
}

/// <summary>
/// How the knowledge search asks the caller which machine they meant, when the scope narrows to more
/// than one and the search itself came back empty. See section 4 of the ambiguity design.
/// </summary>
public sealed record KnowledgeAmbiguityConfiguration
{
    /// <summary>The candidate cap used when the document sets none.</summary>
    public const int DefaultMaxCandidates = 6;

    /// <summary>The per-slot ask cap used when the document sets none.</summary>
    public const int DefaultMaxAsks = 2;

    /// <summary>The probe's own budget used when the document sets none, in seconds.</summary>
    public const int DefaultProbeDeadlineSeconds = 5;

    /// <summary>The loser's extra wait used when the document sets none, in seconds.</summary>
    public const int DefaultProbeWaitMarginSeconds = 1;

    /// <summary>Gets how many distinct values the ask may name. Above this, the ask names none of them.</summary>
    public int MaxCandidates { get; init; } = DefaultMaxCandidates;

    /// <summary>
    /// Gets the cap on a slot's probe ask counter. <c>0</c> means never ask: the gate still works.
    /// </summary>
    public int MaxAsks { get; init; } = DefaultMaxAsks;

    /// <summary>Gets the probe's own budget, in seconds. The main search keeps the store's own deadline.</summary>
    public int ProbeDeadlineSeconds { get; init; } = DefaultProbeDeadlineSeconds;

    /// <summary>
    /// Gets how much longer than the probe, in seconds, a concurrent loser waits for the winner's
    /// payload.
    /// </summary>
    public int ProbeWaitMarginSeconds { get; init; } = DefaultProbeWaitMarginSeconds;
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

    /// <summary>Gets the call store provider, or <see langword="null"/> for the in-process default.</summary>
    public VendorProviderConfiguration? Calls { get; init; }
}
