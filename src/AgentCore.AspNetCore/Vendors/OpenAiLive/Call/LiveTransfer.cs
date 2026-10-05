using AgentCore.Application.Conversation.Actions;
using AgentCore.Application.Runtime.Session;
using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.Vendors.OpenAiLive.Call
{
    /// <summary>
    /// GPT-Live's side of <see cref="TransferAction"/>. A request only records the target: the call's read loop sends the
    /// refer once the caller heard the answer, so the transfer never cuts a reply.
    /// </summary>
    internal sealed class LiveTransfer(Func<Uri, CancellationToken, Task<bool>> refer, TimeSpan wait, Func<bool> leaving, Action scheduled) : IConversationChannel
    {
        private Uri? _target;

        internal Uri? Target => Volatile.Read(ref _target);

        internal TimeSpan Wait => wait;

        public ConversationActionResult Request(ConversationAction action)
        {
            ArgumentNullException.ThrowIfNull(action);
            if (action is not TransferAction transfer)
            {
                return ConversationActionResult.NotSupported;
            }

            // A call that is leaving has no line left to hand over, and a second transfer would contradict the first,
            // which may already be on the line.
            if (leaving() || Interlocked.CompareExchange(ref _target, transfer.Target, null) is not null)
            {
                return ConversationActionResult.Ending;
            }

            // A transfer asked outside a delegation has no answer of its own to leave after.
            scheduled();
            return ConversationActionResult.Scheduled;
        }

        /// <summary>A thrown refer counts as a refused one: either way the line never got the call.</summary>
        internal async Task<bool> ReferAsync(Uri target, ILogger logger, string callId, CancellationToken cancellationToken)
        {
            try
            {
                return await refer(target, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception fault) when (fault is not OperationCanceledException)
            {
                OpenAiLiveLog.ReferFaulted(logger, callId, fault);
                return false;
            }
        }
    }
}
