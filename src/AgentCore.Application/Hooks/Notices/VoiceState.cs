namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>A voice state.</summary>
    public enum VoiceState
    {
        /// <summary>Waiting for speech.</summary>
        Listening,

        /// <summary>Working out a reply.</summary>
        Thinking,

        /// <summary>Speaking.</summary>
        Speaking,

        /// <summary>Not on the call.</summary>
        Away,
    }
}
