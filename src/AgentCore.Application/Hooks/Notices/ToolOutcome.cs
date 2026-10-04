namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>How one tool call ended.</summary>
    public enum ToolOutcome
    {
        /// <summary>The tool returned a result.</summary>
        Ok,

        /// <summary>The tool faulted.</summary>
        Failed,

        /// <summary>A hook blocked the call.</summary>
        Blocked,

        /// <summary>A hook answered in place of the tool.</summary>
        Responded,

        /// <summary>The result came from the tool's cache.</summary>
        Cached,

        /// <summary>The tool ran past its time limit.</summary>
        TimedOut,

        /// <summary>The model called a tool the entry does not declare.</summary>
        Undeclared,
    }
}
