namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>Why a turn was refused and kept none of its words.</summary>
    public enum TurnRefusal
    {
        /// <summary>Another turn held the conversation past the wait limit.</summary>
        Busy,

        /// <summary>The store refused the turn's words: another session saved that turn first.</summary>
        Conflict,

        /// <summary>The client left while the turn waited for the conversation.</summary>
        Gone,

        /// <summary>The caller let go of the turn before it read the reply, so the model never ran.</summary>
        Dropped,

        /// <summary>The conversation had reached a terminal stage.</summary>
        Terminal,

        /// <summary>The session was disposed.</summary>
        Disposed,

        /// <summary>Another entry holds the conversation.</summary>
        InUse,
    }
}
