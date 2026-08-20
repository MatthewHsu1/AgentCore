namespace AgentCore.Application.Configuration.Schema;

/// <summary>What a stage does when no exit guard is true.</summary>
public enum StageNoMatch
{
    /// <summary>The stage stays where it is. This matches <c>OnUnhandledTrigger</c> and is the normal case.</summary>
    Stay,

    /// <summary>The turn fails. A stage sets this to reject the zero-guards-true case.</summary>
    Error,
}

/// <summary>
/// One exit of a stage.
/// </summary>
public sealed record StageTransition
{
    /// <summary>Gets the id of the stage this exit leads to.</summary>
    public required string Stage { get; init; }

    /// <summary>Gets the guard on the exit, or <see langword="null"/> when the exit is unconditional.</summary>
    public GuardReference? When { get; init; }
}

/// <summary>
/// One stage of the machine. The stage names one agent, and the machine picks a stage each turn.
/// </summary>
public sealed record StageConfiguration
{
    /// <summary>Gets the stage id.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the id of the agent that speaks in this stage, or <see langword="null"/>.</summary>
    public string? Agent { get; init; }

    /// <summary>Gets whether the stage ends the call. A terminal stage needs no exit.</summary>
    public bool Terminal { get; init; }

    /// <summary>Gets what the stage does when no exit guard is true.</summary>
    public StageNoMatch OnNoMatch { get; init; } = StageNoMatch.Stay;

    /// <summary>Gets the exits of the stage, in document order. Check 5 evaluates them as siblings.</summary>
    public IReadOnlyList<StageTransition> To { get; init; } = [];
}

/// <summary>
/// The <c>policy:</c> section: a stage machine over string stages, run by <c>Stateless</c>.
/// </summary>
public sealed record PolicyConfiguration
{
    /// <summary>Gets the id of the stage the call starts in.</summary>
    public required string Initial { get; init; }

    /// <summary>Gets the declared stages, in document order.</summary>
    public required IReadOnlyList<StageConfiguration> Stages { get; init; }
}
