namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>A compaction finished or failed.</summary>
    /// <param name="Scope">Where and when it happened.</param>
    /// <param name="MessagesBefore">The message count before.</param>
    /// <param name="MessagesAfter">The message count after.</param>
    /// <param name="Outcome">How it ended.</param>
    public sealed record Compacted(
        HookScope Scope,
        int MessagesBefore,
        int MessagesAfter,
        CompactionOutcome Outcome)
        : HookNotice(Scope);
}
