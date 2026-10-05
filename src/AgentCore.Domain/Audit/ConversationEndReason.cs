namespace AgentCore.Domain.Audit
{
    /// <summary>
    /// The closed set of ways one conversation ends.
    /// </summary>
    public enum ConversationEndReason
    {
        /// <summary>The caller hung up.</summary>
        CallerHungUp = 0,

        /// <summary>The stage machine reached a terminal stage, and the agent finished the conversation.</summary>
        AgentCompleted = 1,

        /// <summary>The conversation went to a human through the conference pattern.</summary>
        TransferredToHuman = 2,

        /// <summary>A fault ended the conversation.</summary>
        Faulted = 3,
    }
}
