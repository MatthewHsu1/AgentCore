using AgentCore.Domain.Audit;
using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.Vendors.OpenAiLive.Call
{
    /// <summary>
    /// How one GPT-Live call ends from this side: at once, or once GPT-Live acked the last piece of an answer and its
    /// speech then went quiet. The timers fire off the read loop; the loop acts on <see cref="Requested"/>.
    /// </summary>
    /// <param name="time">The host's clock.</param>
    /// <param name="callId">The call, for the log.</param>
    /// <param name="logger">Where a late ack is logged.</param>
    internal sealed class LiveCallEnd(TimeProvider time, string callId, ILogger logger) : IDisposable
    {
        private readonly Lock _gate = new();

        private TaskCompletionSource<(ConversationEndReason Reason, string Cause)> _endNow = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // An end asked for while the loop acts on an earlier one, such as during a transfer that may fail and go on.
        private (ConversationEndReason Reason, string Cause)? _endAfter;

        private string? _endAfterAck;

        private ITimer? _ackTimer;

        private ITimer? _quietTimer;

        private bool _disposed;

        private int _finished;

        /// <summary>Gets the end asked for, once one is.</summary>
        internal Task<(ConversationEndReason Reason, string Cause)> Requested
        {
            get
            {
                lock (_gate)
                {
                    return _endNow.Task;
                }
            }
        }

        /// <summary>Gets whether the call finished: nothing goes out on its socket any more.</summary>
        internal bool Finished => Volatile.Read(ref _finished) == 1;

        /// <summary>Disposes the timers; none fires after this.</summary>
        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _ackTimer?.Dispose();
                _quietTimer?.Dispose();
            }
        }

        /// <summary>Marks the call finished.</summary>
        /// <returns><see langword="false"/> when it already was.</returns>
        internal bool TryFinish() => Interlocked.Exchange(ref _finished, 1) == 0;

        internal void Now(ConversationEndReason reason, string cause)
        {
            lock (_gate)
            {
                if (!_endNow.TrySetResult((reason, cause)))
                {
                    _endAfter ??= (reason, cause);
                }
            }
        }

        /// <summary>
        /// Listens for the next end, once the loop chose to go on after the one it was given. An end asked for in the
        /// meantime is the next end.
        /// </summary>
        internal void Rearm()
        {
            lock (_gate)
            {
                _quietTimer?.Dispose();
                _quietTimer = null;
                _ackTimer?.Dispose();
                _ackTimer = null;
                _endAfterAck = null;
                _endNow = new TaskCompletionSource<(ConversationEndReason Reason, string Cause)>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (_endAfter is { } next)
                {
                    _endAfter = null;
                    _ = _endNow.TrySetResult(next);
                }
            }
        }

        /// <summary>GPT-Live is still speaking: a quiet wait already running starts again.</summary>
        internal void AgentSpoke()
        {
            lock (_gate)
            {
                _ = _quietTimer?.Change(OpenAiLiveCall.EndQuietWait, Timeout.InfiniteTimeSpan);
            }
        }

        /// <summary>GPT-Live acked one append; the ack of the last piece starts the quiet wait.</summary>
        internal void Acked(string eventId)
        {
            lock (_gate)
            {
                if (eventId == _endAfterAck)
                {
                    _endAfterAck = null;
                    _ackTimer?.Dispose();
                    _ackTimer = null;
                    _quietTimer?.Dispose();
                    _quietTimer = QuietTimer();
                }
            }
        }

        // Armed before the send, so an ack that comes back at once still finds it. A quiet wait already running is
        // dropped: only the ack of this last piece may start the wait.
        internal void AfterAck(string eventId)
        {
            lock (_gate)
            {
                _quietTimer?.Dispose();
                _quietTimer = null;
                _endAfterAck = eventId;
                _ackTimer?.Dispose();
                _ackTimer = time.CreateTimer(state => AckLate((string)state!), eventId, OpenAiLiveCall.AckWait, Timeout.InfiniteTimeSpan);
            }
        }

        /// <summary>
        /// Hangs up once GPT-Live's speech has been quiet for <see cref="OpenAiLiveCall.EndQuietWait"/>, unless an
        /// answer's own end already waits, or the call already ended or is ending.
        /// </summary>
        internal void WhenQuiet()
        {
            lock (_gate)
            {
                if (_disposed || Finished || _endAfterAck is not null || _quietTimer is not null || _endNow.Task.IsCompleted)
                {
                    return;
                }

                _quietTimer = QuietTimer();
            }
        }

        private ITimer QuietTimer() =>
            time.CreateTimer(_ => Now(ConversationEndReason.AgentCompleted, OpenAiLiveCall.AgentEndedCause), null, OpenAiLiveCall.EndQuietWait, Timeout.InfiniteTimeSpan);

        private void AckLate(string eventId)
        {
            lock (_gate)
            {
                if (eventId != _endAfterAck || Finished)
                {
                    return;
                }

                _endAfterAck = null;
            }

            OpenAiLiveLog.AckLate(logger, callId, OpenAiLiveCall.AckWait.TotalSeconds);
            Now(ConversationEndReason.AgentCompleted, OpenAiLiveCall.AgentEndedCause);
        }
    }
}
