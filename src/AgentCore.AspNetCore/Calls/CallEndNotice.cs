using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Notices;
using Microsoft.Extensions.Logging;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.AspNetCore.Calls
{
    /// <summary>
    /// Raises one call's <see cref="CallEnded"/> once: when the call has started and has begun to leave,
    /// whichever comes last. A call that never started raises none, as it raised no <see cref="CallStarted"/>.
    /// </summary>
    internal sealed class CallEndNotice(string callId, TimeProvider time, ILogger logger)
    {
        private DateTimeOffset _startedAt;

        private int _started;

        private Leave? _leave;

        private int _raised;

        /// <summary>The call started; its length runs from <paramref name="startedAt"/>.</summary>
        internal void Started(DateTimeOffset startedAt)
        {
            _startedAt = startedAt;
            _ = Interlocked.Exchange(ref _started, 1);
            TryRaise();
        }

        /// <summary>The call began to leave. Only the first leave counts.</summary>
        /// <param name="on">The session whose hooks hear of it.</param>
        /// <param name="reason">How the call left.</param>
        /// <param name="cause">The transport's word for why, or <see langword="null"/>.</param>
        internal void Left(ConversationSession on, CallEndReason reason, string? cause)
        {
            if (Volatile.Read(ref _leave) is not null)
            {
                return;
            }

            DateTimeOffset at;
            try
            {
                at = time.GetUtcNow();
            }
            catch (Exception fault) when (fault is not OutOfMemoryException)
            {
                Lose(fault);
                return;
            }

            _ = Interlocked.CompareExchange(ref _leave, new Leave(on, reason, cause, at), null);
            TryRaise();
        }

        /// <summary>
        /// The call began to leave its own session, or found that the id moved on to <paramref name="holder"/>: a newer
        /// session another call holds hears it as <see cref="CallEndReason.Lost"/>, one no call holds as closed.
        /// </summary>
        internal void Left(ConversationSession? holder, ConversationSession own, CallEndReason ownReason, string? cause)
        {
            if (holder is null || ReferenceEquals(holder, own))
            {
                Left(own, ownReason, cause);
            }
            else
            {
                Left(holder, holder.Lifetime.Ending.HasCall ? CallEndReason.Lost : CallEndReason.Closed, cause);
            }
        }

        // Both sides write with a full fence before they read the other's, so at least one of them sees both.
        private void TryRaise()
        {
            if (Volatile.Read(ref _started) == 0 || Volatile.Read(ref _leave) is not { } leave || Interlocked.Exchange(ref _raised, 1) == 1)
            {
                return;
            }

            try
            {
                SessionHooks hooks = leave.On.Hooks;
                CallEnded ended = new(hooks.Scope(turnIndex: null, stage: null), callId, (leave.At - _startedAt).TotalSeconds, leave.Cause, leave.Reason);
                leave.On.Lifetime.Ending.RaiseAfterEnd(ended);
            }
            catch (Exception fault) when (fault is not OutOfMemoryException)
            {
                CallLog.CallEndNoticeLost(logger, callId, fault);
            }
        }

        private void Lose(Exception fault)
        {
            if (Interlocked.Exchange(ref _raised, 1) == 0)
            {
                CallLog.CallEndNoticeLost(logger, callId, fault);
            }
        }

        private sealed record Leave(ConversationSession On, CallEndReason Reason, string? Cause, DateTimeOffset At);
    }
}
