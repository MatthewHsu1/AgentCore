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

    /// <summary>Gets the document version. It is always <see cref="SupportedApiVersion"/>.</summary>
    public required string ApiVersion { get; init; }

    /// <summary>Gets the name of the configured agent.</summary>
    public required string Name { get; init; }

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
}
