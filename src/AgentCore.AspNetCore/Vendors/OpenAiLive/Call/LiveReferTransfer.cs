using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.Vendors.OpenAiLive.Call
{
    /// <summary>
    /// The vendor's own transfer: a SIP refer through OpenAI. OpenAI never reports how a REFER went (probe T1,
    /// docs/probes/live-transfer-t1): its answer is 200 even when the peer declines. So the peer's close of the AI leg
    /// within <paramref name="wait"/> is the transfer, and a call still up after it is a failed one.
    /// </summary>
    internal sealed class LiveReferTransfer(Func<Uri, CancellationToken, Task<bool>> refer, TimeSpan wait, ILogger logger, string callId) : ILiveTransferLine
    {
        public async Task<LiveHandover> HandOverAsync(Uri target, LivePeerClose peer, Task<string?>? receiving, CancellationToken cancellationToken)
        {
            bool sent;
            try
            {
                sent = await refer(target, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception fault) when (fault is not OperationCanceledException)
            {
                OpenAiLiveLog.ReferFaulted(logger, callId, fault);
                sent = false;
            }

            if (!sent)
            {
                return new LiveHandover(LiveHandoverOutcome.NotTaken, receiving, "the refer was refused");
            }

            (bool closed, receiving) = await peer.WaitAsync(wait, receiving, cancellationToken).ConfigureAwait(false);
            return closed
                ? new LiveHandover(LiveHandoverOutcome.TakenAndClosed, null)
                : new LiveHandover(LiveHandoverOutcome.NotTaken, receiving, "the call was still up when the transfer wait ran out");
        }
    }
}
