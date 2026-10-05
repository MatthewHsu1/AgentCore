using AgentCore.AspNetCore.Calls;
using AgentCore.Domain.Audit;
using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.Vendors.OpenAiLive.Call
{
    /// <summary>
    /// Ends one GPT-Live call exactly once: hangs the line up from this side when asked, ends the call core, waits for
    /// the answers in flight, and closes the conversation.
    /// </summary>
    internal sealed class LiveCallFinish(PhoneCall call, LiveCallEnd end, LiveAnswers answers, Func<CancellationToken, Task<bool>> hangUp, ILogger logger)
    {
        private int _hungUp;

        internal async Task HangUpAndFinishAsync(ConversationEndReason reason, string cause, CancellationToken cancellationToken)
        {
            if (!end.Finished && Interlocked.Exchange(ref _hungUp, 1) == 0)
            {
                bool hungUp;
                try
                {
                    hungUp = await hangUp(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception fault) when (fault is not OutOfMemoryException)
                {
                    OpenAiLiveLog.HangupFaulted(logger, call.CallId, fault);
                    hungUp = true;
                }

                if (!hungUp)
                {
                    OpenAiLiveLog.HangupFailed(logger, call.CallId);
                }
            }

            await FinishAsync(reason, cause).ConfigureAwait(false);
        }

        internal async Task FinishAsync(ConversationEndReason reason, string cause)
        {
            if (!end.TryFinish())
            {
                return;
            }

            await call.EndAsync(reason, cause).ConfigureAwait(false);
            await answers.WhenAnsweredAsync().ConfigureAwait(false);

            try
            {
                await call.CloseAsync().ConfigureAwait(false);
            }
            // The close takes no token, so a cancellation here is the store's own timeout.
            catch (Exception fault) when (fault is not OutOfMemoryException)
            {
                OpenAiLiveLog.CloseFaulted(logger, call.CallId, fault);
            }
        }
    }
}
