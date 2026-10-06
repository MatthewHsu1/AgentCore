using AgentCore.Application.Conversation.Commands;

namespace AgentCore.AspNetCore.Vendors.OpenAiLive.Call
{
    /// <summary>
    /// GPT-Live's side of <see cref="TransferCommand"/>. A send only records the command: the call's read loop hands the
    /// caller over through <see cref="Line"/> once the caller heard the answer, so the transfer never cuts a reply.
    /// </summary>
    internal sealed class LiveTransfer(ILiveTransferLine line, Func<bool> leaving, Action scheduled)
    {
        private TransferCommand? _pending;

        internal ILiveTransferLine Line => line;

        internal TransferCommand? Pending => Volatile.Read(ref _pending);

        internal Uri? Target => Pending?.Target;

        internal string? IfFailed => Pending?.IfFailed;

        internal ChannelCommandResult Send(TransferCommand transfer)
        {
            // A call that is leaving has no line left to hand over, and a second transfer would contradict the first,
            // which may already be on the line.
            if (leaving() || Interlocked.CompareExchange(ref _pending, transfer, null) is not null)
            {
                return ChannelCommandResult.Ending;
            }

            // A transfer asked outside a delegation has no answer of its own to leave after.
            scheduled();
            return ChannelCommandResult.Scheduled;
        }

        /// <summary>The line did not take the call and the call goes on, so the caller's next words start turns again.</summary>
        internal void Failed()
        {
            Volatile.Write(ref _pending, null);
        }
    }
}
