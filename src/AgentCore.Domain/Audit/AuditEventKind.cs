namespace AgentCore.Domain.Audit
{
    /// <summary>
    /// The closed set of things one conversation writes into the audit chain.
    /// </summary>
    public enum AuditEventKind
    {
        /// <summary>A session of the conversation started. It is the first event of every session, not of every conversation.</summary>
        ConversationStarted = 0,

        /// <summary>One turn ran to the end, and the caller heard the whole reply.</summary>
        TurnCompleted = 1,

        /// <summary>The caller spoke over a reply, so the reply stopped early.</summary>
        ReplyInterrupted = 2,

        /// <summary>A tool call failed, and the turn continued without its answer.</summary>

        ToolFailed = 3,

        /// <summary>The host ended the conversation. It is the last event of the session that ended it, not of the chain.</summary>
        ConversationEnded = 5,

        /// <summary>Moderation flagged what the CALLER said, so the agent refused to answer that turn.</summary>
        PromptFlagged = 6,

        /// <summary>A caller replaced an earlier message, so the turns after it were withdrawn.</summary>
        TurnSuperseded = 7,
    }
}
