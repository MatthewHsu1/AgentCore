namespace AgentCore.Application.Calls;

/// <summary>What one slot's knowledge probe has already spent of its ask budget (§8).</summary>
/// <remarks>
/// Carried on <see cref="CallSessionState"/> so a caller who drops and reconnects mid-call is not
/// asked the same clarification a second <c>maxAsks</c> times. What was last named to the caller is
/// deliberately absent: it belongs to a turn the reconnected caller is no longer in.
/// </remarks>
public sealed record CallClarificationState
{
    /// <summary>Gets how many times the knowledge probe has offered this slot (§8, K22).</summary>
    public int ProbeAsks { get; init; }
}
