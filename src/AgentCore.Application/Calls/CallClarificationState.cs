namespace AgentCore.Application.Calls;

/// <summary>What one slot's ambiguity channels have already spent of their ask budget (§7, §8).</summary>
/// <remarks>
/// Carried on <see cref="CallSessionState"/> so a caller who drops and reconnects mid-call is not
/// asked the same clarification a second <c>maxAsks</c> times. What was last named to the caller is
/// deliberately absent: it belongs to a turn the reconnected caller is no longer in.
/// </remarks>
public sealed record CallClarificationState
{
    /// <summary>Gets how many times the knowledge probe has offered this slot (§8, K22).</summary>
    public int ProbeAsks { get; init; }

    /// <summary>Gets how many times the clarification instruction has named this slot (§7).</summary>
    public int NamedAsks { get; init; }

    /// <summary>Gets whether §7 step 3's one counter reset has already been spent.</summary>
    public bool ResetSpent { get; init; }
}
