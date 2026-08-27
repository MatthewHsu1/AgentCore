namespace AgentCore.Application.Configuration.Schema;

/// <summary>How an agent reaches the knowledge base.</summary>
public enum KnowledgeMode
{
    /// <summary>Retrieve before the model is called, and inject the cards. One model round trip.</summary>
    Prefetch,

    /// <summary>Give the model a search tool and let it decide.</summary>
    Tool,
}

/// <summary>
/// One agent's <c>knowledge:</c> block. Every field is optional, so an unset field is distinct
/// from one set to its default and can inherit key by key.
/// </summary>
public sealed record AgentKnowledgeConfiguration
{
    /// <summary>
    /// The largest value <see cref="Limit"/> may carry.
    /// </summary>
    public const int MaximumLimit = 20;

    /// <summary>Gets the mode, or <see langword="null"/> to inherit.</summary>
    public KnowledgeMode? Mode { get; init; }

    /// <summary>Gets how many cards to retrieve, or <see langword="null"/> to inherit.</summary>
    public int? Limit { get; init; }

    /// <summary>Gets whether the model sees the source label, or <see langword="null"/> to inherit.</summary>
    public bool? Citations { get; init; }

    /// <summary>Gets whether this agent's searches are confined to a scope, or <see langword="null"/> to inherit.</summary>
    public bool? Scoped { get; init; }
}

/// <summary>
/// The settings every agent inherits.
/// </summary>
public sealed record AgentDefaults
{
    /// <summary>Gets the model every agent uses unless it names its own.</summary>
    public ModelReference? Model { get; init; }

    /// <summary>Gets the shared instruction prefix, or <see langword="null"/>.</summary>
    public string? Instructions { get; init; }

    /// <summary>Gets the shared <c>knowledge:</c> block, or <see langword="null"/> when the document declares none.</summary>
    public AgentKnowledgeConfiguration? Knowledge { get; init; }
}

/// <summary>
/// One declared agent.
/// </summary>
public sealed record AgentConfiguration
{
    /// <summary>Gets the agent id. A stage or a graph node names this id.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the instruction delta this agent appends below the shared prefix.</summary>
    public string? Instructions { get; init; }

    /// <summary>Gets the model of this agent, or <see langword="null"/> to inherit the default.</summary>
    public ModelReference? Model { get; init; }

    /// <summary>Gets the ids of the tools this agent may call.</summary>
    public IReadOnlyList<string> Tools { get; init; } = [];

    /// <summary>Gets this agent's <c>knowledge:</c> block, or <see langword="null"/> to inherit key by key.</summary>
    public AgentKnowledgeConfiguration? Knowledge { get; init; }
}

/// <summary>
/// The <c>agents:</c> section.
/// </summary>
public sealed record AgentsConfiguration
{
    /// <summary>Gets the shared settings, or <see langword="null"/> when the document declares none.</summary>
    public AgentDefaults? Defaults { get; init; }

    /// <summary>Gets the declared agents, in document order.</summary>
    public required IReadOnlyList<AgentConfiguration> Items { get; init; }
}
