namespace AgentCore.Application.Conversation.Commands
{
    /// <summary>
    /// How a host's transfer went, as its <see cref="IChannelCommandHandler{TCommand, TOutcome}"/> answers it. A channel
    /// reads every value it does not know as <see cref="NotTaken"/>.
    /// </summary>
    public enum CallTransferOutcome
    {
        /// <summary>The line did not take the call; the caller is still on this call.</summary>
        NotTaken = 0,

        /// <summary>The line took the call; this side hangs its leg up.</summary>
        Taken = 1,
    }
}
