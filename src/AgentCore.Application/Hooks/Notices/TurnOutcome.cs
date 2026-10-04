namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>How a turn ended.</summary>
    public enum TurnOutcome
    {
        /// <summary>The model answered.</summary>
        Answered,

        /// <summary>The turn spoke the fallback reply.</summary>
        Fallback,

        /// <summary>The model produced no reply.</summary>
        Empty,

        /// <summary>A hook blocked the turn.</summary>
        Blocked,

        /// <summary>The turn was cancelled.</summary>
        Cancelled,

        /// <summary>The turn faulted.</summary>
        Faulted,
    }
}
