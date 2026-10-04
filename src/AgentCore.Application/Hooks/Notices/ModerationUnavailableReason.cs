namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>Why moderation had no verdict.</summary>
    public enum ModerationUnavailableReason
    {
        /// <summary>The moderation call ran past its limit.</summary>
        TimedOut,

        /// <summary>The moderation call threw.</summary>
        Threw,
    }
}
