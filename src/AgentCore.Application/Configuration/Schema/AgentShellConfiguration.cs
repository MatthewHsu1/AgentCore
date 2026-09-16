namespace AgentCore.Application.Configuration.Schema;

/// <summary>The executors a <c>shell:</c> block may name.</summary>
public enum ShellKind
{
    /// <summary>A throwaway container: network "none", read-only root, nobody user (§3.6).</summary>
    Docker,

    /// <summary>The process's own executable environment, confined to the workspace directory.</summary>
    Local,
}

/// <summary>
/// One agent's shell policy: the regexes that refuse a command before it dispatches. A guardrail
/// for the model, not a security boundary — the executor confinement is the boundary.
/// </summary>
public sealed record ShellPolicyConfiguration
{
    /// <summary>Gets the patterns whose match refuses a command.</summary>
    public IReadOnlyList<string> Deny { get; init; } = [];

    /// <summary>Gets the patterns an allowed command must match.</summary>
    public IReadOnlyList<string> Allow { get; init; } = [];
}

/// <summary>One agent's <c>shell:</c> block: one executor per call, removed with it (§4.4).</summary>
public sealed record ShellConfiguration
{
    /// <summary>
    /// Gets the executor kind. Deliberately required: where a shell runs is the consumer's call, so
    /// no default exists here (§4.5.1).
    /// </summary>
    public required ShellKind Kind { get; init; }

    /// <summary>Gets the command policy, or <see langword="null"/> for none.</summary>
    public ShellPolicyConfiguration? Policy { get; init; }

    /// <summary>Gets how long one command may run, or <see langword="null"/> for the executor default.</summary>
    public int? TimeoutSeconds { get; init; }
}