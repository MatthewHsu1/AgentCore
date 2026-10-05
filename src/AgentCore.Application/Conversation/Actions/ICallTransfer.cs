namespace AgentCore.Application.Conversation.Actions
{
    /// <summary>
    /// A host that moves a call to another line itself, such as through its phone system's API. A phone channel that can
    /// transfer (GPT-Live today) hands a registered one each <see cref="TransferAction"/> in place of its own transfer,
    /// and takes its answer as the outcome: the vendor's own transfer may report none (OpenAI's SIP refer, probe T1).
    /// Register it as a singleton: a call outlives the request that started it.
    /// </summary>
    public interface ICallTransfer
    {
        /// <summary>Moves the call. It runs once the caller heard the answer that asked for it.</summary>
        /// <param name="transfer">The call and its target.</param>
        /// <param name="cancellationToken">Cancels the move.</param>
        /// <returns>Whether the line took the call.</returns>
        Task<CallTransferOutcome> TransferAsync(CallTransfer transfer, CancellationToken cancellationToken);
    }
}
