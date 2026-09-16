namespace AgentCore.Application.Configuration.Schema;

/// <summary>
/// One named entry. The entry key is the agent name.
/// </summary>
public sealed record EntryConfiguration
{
    /// <summary>Gets the id of the agent in the shared pool, or <see langword="null"/>.</summary>
    public string? Agent { get; init; }

    /// <summary>Gets the stage machine, or <see langword="null"/> when the entry declares none.</summary>
    public PolicyConfiguration? Policy { get; init; }

    /// <summary>Gets the workflow graph, or <see langword="null"/> when the entry declares none.</summary>
    public GraphConfiguration? Graph { get; init; }

    /// <summary>Gets the entry override for the spoken fallback, or <see langword="null"/> to inherit the root line.</summary>
    public string? FallbackReply { get; init; }

    /// <summary>Gets the entry override for the spoken refusal, or <see langword="null"/> to inherit the root line.</summary>
    public string? RefusalReply { get; init; }
}
