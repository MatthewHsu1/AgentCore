using AgentCore.Application.Conversation.Actions;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.AspNetCore.Vendors.OpenAiLive.Call
{
    /// <summary>
    /// GPT-Live's side of <see cref="TransferAction"/>. A request only records the action: the call's read loop hands the
    /// caller over through <see cref="Line"/> once the caller heard the answer, so the transfer never cuts a reply.
    /// </summary>
    internal sealed class LiveTransfer(ILiveTransferLine line, Func<bool> leaving, Action scheduled) : IConversationChannel
    {
        private TransferAction? _pending;

        internal ILiveTransferLine Line => line;

        internal Uri? Target => Volatile.Read(ref _pending)?.Target;

        internal string? IfFailed => Volatile.Read(ref _pending)?.IfFailed;

        public ConversationActionResult Request(ConversationAction action)
        {
            ArgumentNullException.ThrowIfNull(action);
            if (action is not TransferAction transfer)
            {
                return ConversationActionResult.NotSupported;
            }

            // A call that is leaving has no line left to hand over, and a second transfer would contradict the first,
            // which may already be on the line.
            if (leaving() || Interlocked.CompareExchange(ref _pending, transfer, null) is not null)
            {
                return ConversationActionResult.Ending;
            }

            // A transfer asked outside a delegation has no answer of its own to leave after.
            scheduled();
            return ConversationActionResult.Scheduled;
        }

        /// <summary>The line did not take the call and the call goes on, so the caller's next words start turns again.</summary>
        internal void Failed() => Volatile.Write(ref _pending, null);
    }
}
