using AgentCore.Application.Runtime;

namespace AgentCore.Application.Sessions.Memory
{
    /// <summary>One session that <see cref="InMemoryConversationSessions"/> holds, with the idle timer that ends it.</summary>
    internal sealed class HeldSession : IDisposable
    {
        private readonly Lock _gate = new();

        private readonly TimeSpan _idleTimeout;

        private readonly TimeProvider _time;

        private readonly Action<HeldSession> _expired;
        
        private readonly ITimer _timer;

        // A GetTimestamp value, not a DateTimeOffset, so that a wall-clock jump neither expires nor keeps a session.
        private long _touched;

        // Set when the timer is gone. The session is still handed out until it is also ended.
        private bool _stopped;

        // Set once the session expired or the host closed it. Nothing may hand the session out after that.
        private bool _ended;

        /// <summary>Holds one session and arms its idle timer.</summary>
        /// <param name="session">The session to hold.</param>
        /// <param name="idleTimeout">How long the session may stay untouched.</param>
        /// <param name="time">The clock the timer runs on.</param>
        /// <param name="expired">Called once, off the gate, when the session expired.</param>
        internal HeldSession(ConversationSession session, TimeSpan idleTimeout, TimeProvider time, Action<HeldSession> expired)
        {
            Session = session;
            _idleTimeout = idleTimeout;
            _time = time;
            _expired = expired;

            lock (_gate)
            {
                _touched = time.GetTimestamp();
                _timer = CreateTimer();
            }
        }

        /// <summary>Gets the session held.</summary>
        internal ConversationSession Session { get; }

        /// <summary>Restarts the idle clock.</summary>
        /// <returns><see langword="false"/> once the session has ended. The caller must not hand it out then.</returns>
        internal bool TryTouch()
        {
            lock (_gate)
            {
                if (_ended)
                {
                    return false;
                }

                if (!_stopped)
                {
                    _touched = _time.GetTimestamp();
                    _ = _timer.Change(_idleTimeout, Timeout.InfiniteTimeSpan);
                }

                return true;
            }
        }

        /// <summary>Ends the session because the host closed it. Its timer never fires after this.</summary>
        internal void End()
        {
            lock (_gate)
            {
                _ended = true;
                StopLocked();
            }
        }

        /// <summary>Stops the timer. The session stays held, and a lookup still finds it.</summary>
        public void Dispose()
        {
            lock (_gate)
            {
                StopLocked();
            }
        }

        private void OnIdle()
        {
            Task? turn;

            lock (_gate)
            {
                if (_stopped)
                {
                    return;
                }

                // A touch can re-arm the timer after it came due but before this callback ran.
                TimeSpan idle = _time.GetElapsedTime(_touched);
                if (idle < _idleTimeout)
                {
                    _ = _timer.Change(_idleTimeout - idle, Timeout.InfiniteTimeSpan);
                    return;
                }

                turn = Session.Cuts.RunningTurn();
                if (turn is null)
                {
                    _ended = true;
                    StopLocked();
                }
            }

            if (turn is null)
            {
                _expired(this);
                return;
            }

            // The idle clock restarts when the turn frees the slot. Closing now would wait on the turn anyway.
            _ = turn.ContinueWith(
                static (_, state) => ((HeldSession)state!).TryTouch(),
                this,
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }

        private void StopLocked()
        {
            if (!_stopped)
            {
                _stopped = true;
                _timer.Dispose();
            }
        }

        private ITimer CreateTimer()
        {
            // The timer would otherwise capture the context of the request that opened the session. The expiry
            // would then run half an hour later inside that request's trace and logging scopes.
            bool restoreFlow = false;
            try
            {
                if (!ExecutionContext.IsFlowSuppressed())
                {
                    _ = ExecutionContext.SuppressFlow();
                    restoreFlow = true;
                }

                return _time.CreateTimer(
                    static state => ((HeldSession)state!).OnIdle(), this, _idleTimeout, Timeout.InfiniteTimeSpan);
            }
            finally
            {
                if (restoreFlow)
                {
                    ExecutionContext.RestoreFlow();
                }
            }
        }
    }
}
