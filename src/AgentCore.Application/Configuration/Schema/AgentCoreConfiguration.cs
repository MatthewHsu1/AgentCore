using System.Text.Json.Nodes;

namespace AgentCore.Application.Configuration.Schema;

/// <summary>
/// One <c>apiVersion: agentcore/v1</c> configuration document, bound to records.
/// </summary>
/// <remarks>
/// The parser produces this after check 1 of section 8.5 passes. Checks 2 to 8 read it, and the
/// compile table in section 8.2 turns it into an agent.
/// </remarks>
public sealed record AgentCoreConfiguration
{
    /// <summary>The only <c>apiVersion</c> value this release accepts.</summary>
    public const string SupportedApiVersion = "agentcore/v1";

    /// <summary>The spoken fallback used when the document names none.</summary>
    /// <remarks>
    /// Section 8.7 asks for a spoken fallback and names no text. This sentence is short, it is
    /// speakable, and it asks the caller to go on, so the call survives the turn that failed.
    /// </remarks>
    public const string DefaultFallbackReply = "I am sorry. I could not finish that. Please say it again.";

    /// <summary>Gets the document version. It is always <see cref="SupportedApiVersion"/>.</summary>
    public required string ApiVersion { get; init; }

    /// <summary>Gets the name of the configured agent.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the line the caller hears when a turn fails.</summary>
    /// <remarks>
    /// Section 8.7 names two failures that end a turn with a spoken fallback: a run that returns no
    /// text after 40 tool rounds, and a tool that fails four times in a row. The key sits at the root
    /// of the document because every shape speaks it: a single agent, a <c>policy:</c> document, and
    /// a <c>graph:</c> document all end a failed turn the same way. It defaults to
    /// <see cref="DefaultFallbackReply"/>, and a document may not set an empty line.
    /// </remarks>
    public string FallbackReply { get; init; } = DefaultFallbackReply;

    /// <summary>Gets the declared state slots, keyed by slot name.</summary>
    public EquatableDictionary<StateSlotConfiguration> State { get; init; } = EquatableDictionary<StateSlotConfiguration>.Empty;

    /// <summary>Gets the extractor settings, or <see langword="null"/> when the document declares none.</summary>
    public ExtractorConfiguration? Extractor { get; init; }

    /// <summary>Gets the named guards, keyed by guard name. Each value is a raw JSONLogic rule.</summary>
    public EquatableDictionary<JsonNode> Guards { get; init; } = EquatableDictionary<JsonNode>.Empty;

    /// <summary>Gets the declared tools, in document order.</summary>
    public EquatableList<ToolConfiguration> Tools { get; init; } = EquatableList<ToolConfiguration>.Empty;

    /// <summary>Gets the agent section, or <see langword="null"/> when the document declares none.</summary>
    public AgentsConfiguration? Agents { get; init; }

    /// <summary>Gets the stage machine, or <see langword="null"/> when the document declares none.</summary>
    public PolicyConfiguration? Policy { get; init; }

    /// <summary>Gets the workflow graph, or <see langword="null"/> when the document declares none.</summary>
    public GraphConfiguration? Graph { get; init; }

    /// <summary>Gets the adapter settings, or <see langword="null"/> when the document declares none.</summary>
    public ProvidersConfiguration? Providers { get; init; }

    /// <summary>Gets the evaluation settings, or <see langword="null"/> when the document declares none.</summary>
    public EvaluationConfiguration? Evaluation { get; init; }
}
