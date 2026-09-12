namespace AgentCore.Application.Configuration.Schema;

/// <summary>
/// The options one <c>loop.until:</c> entry carries. Empty today: the entry names the condition by
/// its key. A condition that ever takes options adds members here.
/// </summary>
public sealed record LoopConditionOptions { }

/// <summary>One <c>loop.until:</c> entry. Exactly one member is set.</summary>
public sealed record LoopUntilConfiguration
{
    /// <summary>Gets the condition that the todo list drains to empty. Set, or <see langword="null"/>.</summary>
    public LoopConditionOptions? Todos { get; init; }

    /// <summary>Gets the condition that every background task finishes. Set, or <see langword="null"/>.</summary>
    public LoopConditionOptions? Background { get; init; }
}

/// <summary>One agent's <c>loop:</c> block: how many model rounds the agent owns may run, and when none stays.</summary>
public sealed record LoopConfiguration
{
    /// <summary>Gets the most rounds the loop may run, or <see langword="null"/> for the framework default.</summary>
    public int? MaxRounds { get; init; }

    /// <summary>Gets the conditions that end the loop early, in document order, or <see langword="null"/> for none.</summary>
    public IReadOnlyList<LoopUntilConfiguration>? Until { get; init; }
}