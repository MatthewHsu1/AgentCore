namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>The turn was sealed.</summary>
    /// <param name="Scope">Where and when it happened.</param>
    /// <param name="Outcome">How the turn ended.</param>
    /// <param name="UserText">The caller's words. The text is the caller's conversation text: a hook that logs it logs personal data.</param>
    /// <param name="ReplyText">The words the turn's reply rows hold. For a cut turn, the whole text the model produced; <see cref="ReplyCut"/> carries what was heard. It is the caller's conversation text: a hook that logs it logs personal data.</param>
    /// <param name="StageBefore">The stage when the turn began.</param>
    /// <param name="StageAfter">The stage when the turn ended.</param>
    /// <param name="Duration">How long the turn took.</param>
    /// <param name="Failure">The reason for <see cref="TurnOutcome.Fallback"/>, <see cref="TurnOutcome.Empty"/> and <see cref="TurnOutcome.Faulted"/>, or null.</param>
    /// <param name="FailedInTool">Whether the fallback came from a tool that spent its retry budget.</param>
    /// <param name="Cause">The exception behind the failure, for a log's stack trace, or null.</param>
    public sealed record TurnCompleted(
        HookScope Scope,
        TurnOutcome Outcome,
        string UserText,
        string ReplyText,
        string StageBefore,
        string StageAfter,
        TimeSpan Duration,
        string? Failure,
        bool FailedInTool,
        Exception? Cause)
        : HookNotice(Scope);
}
