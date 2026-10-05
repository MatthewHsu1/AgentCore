namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>Why a session left memory.</summary>
    public enum UnloadCause
    {
        /// <summary>The session sat idle past its limit.</summary>
        Idle,

        /// <summary>The session was closed.</summary>
        Closed,
    }
}
