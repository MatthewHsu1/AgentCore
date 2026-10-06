using AgentCore.Application.Conversation.Commands;

namespace AgentCore.AspNetCore.Vendors.OpenAiLive.Call
{
    /// <summary>
    /// How one GPT-Live call hands the caller to another line, and how it learns whether the line took the call. It runs
    /// once the caller heard the answer that asked for the transfer, and never throws for a line that did not take it.
    /// </summary>
    internal interface ILiveTransferLine
    {
        /// <param name="transfer">The command, as the tool sent it.</param>
        /// <param name="peer">Waits for the peer to close the AI leg, for a line whose only sign of success is that close.</param>
        /// <param name="receiving">The read loop's receive still open, or <see langword="null"/>.</param>
        /// <param name="cancellationToken">Cancels the hand-over.</param>
        Task<LiveHandover> HandOverAsync(TransferCommand transfer, LivePeerClose peer, Task<string?>? receiving, CancellationToken cancellationToken);
    }
}
