using AgentCore.Application.Conversation.Actions;
using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.Vendors.OpenAiLive.Call
{
    /// <summary>
    /// The host's own transfer, through its <see cref="ICallTransfer"/>. Its answer is the outcome, so nothing waits for
    /// the peer's close, and the AI leg is hung up from this side once the line took the call.
    /// </summary>
    internal sealed class LiveHostTransfer(
        ICallTransfer host, string conversationId, IReadOnlyDictionary<string, string> headers, ILogger logger, string callId) : ILiveTransferLine
    {
        public async Task<LiveHandover> HandOverAsync(Uri target, LivePeerClose peer, Task<string?>? receiving, CancellationToken cancellationToken)
        {
            CallTransferOutcome outcome;
            try
            {
                outcome = await host.TransferAsync(
                    new CallTransfer { ConversationId = conversationId, Target = target, Headers = headers }, cancellationToken).ConfigureAwait(false);
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
