using System.Collections.ObjectModel;
using System.Text.Json.Nodes;

namespace AgentCore.Application.Configuration.Schema;

/// <summary>
/// One <c>apiVersion: agentcore/v1</c> configuration document, bound to records.
/// </summary>
/// <remarks>
/// <para>
/// The parser produces this after check 1 of section 8.5 passes. Checks 2 to 8 read it, and the
/// compile table in section 8.2 turns it into an agent.
/// </para>
/// <para>
/// <b>Do not compare two of these for equality.</b> The collection properties are BCL interfaces,
/// so the compiler-written record equality compares them by reference and two loads of the same
/// document are never equal. Nothing in AgentCore compares two configurations, and rule 17 of
/// section 11 — the same document loads identically as YAML and as JSON — is proved where it is
/// stronger: on the raw document trees, through <see cref="JsonNode.DeepEquals(JsonNode, JsonNode)"/>,
/// which also catches keys these records do not model. See <c>ConfigurationRoundTripTests</c>.
/// </para>
/// </remarks>
public sealed record AgentCoreConfiguration
{
    /// <summary>The only <c>apiVersion</c> value this release accepts.</summary>
    public const string SupportedApiVersion = "agentcore/v1";

    /// <summary>The spoken fallback used when the document names none.</summary>
    public const string DefaultFallbackReply = "I am sorry. I could not finish that. Please say it again.";

    /// <summary>The spoken refusal used when the document names none.</summary>
    public const string DefaultRefusalReply = "I am sorry. I cannot help with that request.";

    /// <summary>Gets the document version. It is always <see cref="SupportedApiVersion"/>.</summary>
    public required string ApiVersion { get; init; }

    /// <summary>Gets the line the caller hears when a turn fails.</summary>
    public string FallbackReply { get; init; } = DefaultFallbackReply;

    /// <summary>Gets the line the caller hears when the agent refuses to answer.</summary>
    public string RefusalReply { get; init; } = DefaultRefusalReply;

    /// <summary>Gets the declared state slots, keyed by slot name.</summary>
    public IReadOnlyDictionary<string, StateSlotConfiguration> State { get; init; } = ReadOnlyDictionary<string, StateSlotConfiguration>.Empty;

    /// <summary>Gets the extractor settings, or <see langword="null"/> when the document declares none.</summary>
    public ExtractorConfiguration? Extractor { get; init; }

    /// <summary>Gets the named guards, keyed by guard name. Each value is a raw JSONLogic rule.</summary>
    public IReadOnlyDictionary<string, JsonNode> Guards { get; init; } = ReadOnlyDictionary<string, JsonNode>.Empty;

    /// <summary>Gets the declared tools, in document order.</summary>
    public IReadOnlyList<ToolConfiguration> Tools { get; init; } = [];

    /// <summary>Gets the declared MCP servers, in document order.</summary>
    public IReadOnlyList<McpServerConfiguration> Mcp { get; init; } = [];

    /// <summary>Gets the shared agent pool.</summary>
    public required AgentsConfiguration Agents { get; init; }

    /// <summary>Gets the named entries, keyed by entry name. The key is the agent name.</summary>
    public required IReadOnlyDictionary<string, EntryConfiguration> Entries { get; init; }

    /// <summary>Gets the adapter settings, or <see langword="null"/> when the document declares none.</summary>
    public ProvidersConfiguration? Providers { get; init; }

    /// <summary>Gets the evaluation settings, or <see langword="null"/> when the document declares none.</summary>
    public EvaluationConfiguration? Evaluation { get; init; }

    /// <summary>Gets the titler settings, or <see langword="null"/> when the document declares none.</summary>
    public TitlerConfiguration? Titler { get; init; }
}
