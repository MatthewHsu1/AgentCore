namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>How a compaction ended.</summary>
    public enum CompactionOutcome
    {
        /// <summary>The history shrank.</summary>
        Compacted,

        /// <summary>The history stayed as it was.</summary>
        Unchanged,

        /// <summary>The compaction faulted.</summary>
        Failed,

        /// <summary>The compaction was cancelled.</summary>
        Cancelled,
    }
}
