namespace AgentCore.Application.Configuration.Schema;

/// <summary>
/// The settings every agent inherits.
/// </summary>
/// <remarks>
/// <see cref="Instructions"/> is the stable cached prefix, and each stage appends a delta below it.
/// Anything that changes per turn must sit below the prefix, or it defeats the cache and costs money.
/// </remarks>
public sealed record AgentDefaults
{
    /// <summary>Gets the model every agent uses unless it names its own.</summary>
    public ModelReference? Model { get; init; }

    /// <summary>Gets the shared instruction prefix, or <see langword="null"/>.</summary>
    public string? Instructions { get; init; }
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
