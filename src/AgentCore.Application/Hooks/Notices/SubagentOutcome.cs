namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>How an agent-as-tool run ended.</summary>
    public enum SubagentOutcome
    {
        /// <summary>The sub-agent returned an answer.</summary>
        Answered,

        /// <summary>The sub-agent faulted.</summary>
        Faulted,

        /// <summary>The sub-agent was cancelled.</summary>
        Cancelled,
    }
}
