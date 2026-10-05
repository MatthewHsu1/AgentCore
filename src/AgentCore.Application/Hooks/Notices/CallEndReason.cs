namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>How a call left its conversation.</summary>
    public enum CallEndReason
    {
        /// <summary>The call ended together with its conversation, which raises <see cref="ConversationEnded"/>.</summary>
        Ended,

        /// <summary>A newer connection with the same call id took the call over; the conversation goes on under it.</summary>
        Replaced,

        /// <summary>Another call took the conversation after it unloaded; that call's conversation is left alone.</summary>
        Lost,

        /// <summary>The call left without ending its conversation, which was closed instead.</summary>
        Closed,
    }
}
