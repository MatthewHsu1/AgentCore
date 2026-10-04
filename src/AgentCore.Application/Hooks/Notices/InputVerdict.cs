namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>What moderation decided about the caller's words.</summary>
    public enum InputVerdict
    {
        /// <summary>Moderation passed the words.</summary>
        Clean,

        /// <summary>Moderation flagged the words.</summary>
        Flagged,

        /// <summary>Moderation gave no verdict.</summary>
        Unavailable,
    }
}
