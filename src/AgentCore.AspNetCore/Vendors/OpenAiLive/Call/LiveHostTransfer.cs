using AgentCore.Application.Conversation.Commands;
using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.Vendors.OpenAiLive.Call
{
    /// <summary>
    /// The host's own transfer, through its <see cref="IChannelCommandHandler{TCommand, TOutcome}"/>. Its answer is the
    /// outcome, so nothing waits for the peer's close, and the AI leg is hung up from this side once the line took the call.
    /// </summary>
    internal sealed class LiveHostTransfer(
        IChannelCommandHandler<TransferCommand, CallTransferOutcome> host,
        string conversationId,
        IReadOnlyDictionary<string, string> headers,
        ILogger logger,
        string callId) : ILiveTransferLine
    {
        public async Task<LiveHandover> HandOverAsync(TransferCommand transfer, LivePeerClose peer, Task<string?>? receiving, CancellationToken cancellationToken)
        {
            CallTransferOutcome outcome;
            try
            {
                outcome = await host.HandleAsync(
                    transfer, new ChannelCommandContext { ConversationId = conversationId, Headers = headers }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception fault) when (fault is not OperationCanceledException)
            {
                OpenAiLiveLog.ReferFaulted(logger, callId, fault);
                outcome = CallTransferOutcome.NotTaken;
            }

            return outcome == CallTransferOutcome.Taken
                ? new LiveHandover(LiveHandoverOutcome.Taken, receiving)
                : new LiveHandover(LiveHandoverOutcome.NotTaken, receiving, "the host's line did not take the call");
        }
    }
}
