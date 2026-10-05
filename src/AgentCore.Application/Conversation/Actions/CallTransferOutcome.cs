namespace AgentCore.Application.Conversation.Actions
{
    /// <summary>How an <see cref="ICallTransfer"/> went. A channel reads every value it does not know as <see cref="NotTaken"/>.</summary>
    public enum CallTransferOutcome
    {
        /// <summary>The line did not take the call; the caller is still on this call.</summary>
        NotTaken = 0,

        /// <summary>The line took the call; this side hangs its leg up.</summary>
        Taken = 1,
    }
}
