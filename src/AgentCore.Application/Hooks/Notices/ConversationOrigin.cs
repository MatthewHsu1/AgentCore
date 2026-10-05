namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>How a session found its conversation.</summary>
    public enum ConversationOrigin
    {
        /// <summary>The store held nothing.</summary>
        New,

        /// <summary>The store held rows or state.</summary>
        Resumed,

        /// <summary>This process held the conversation in an earlier session.</summary>
        Reloaded,

        /// <summary>
        /// The session ended or refused a turn before its store was opened, so whether the conversation was new or
        /// resumed is unknown.
        /// </summary>
        Unopened,
    }
}
