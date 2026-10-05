using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Sessions.Memory
{
    /// <summary>One session that <see cref="InMemoryConversationSessions"/> holds, with the idle timer that unloads it.</summary>
    internal sealed class HeldSession : IDisposable
    {
        /// <summary>
        /// How often the idle check polls for a <c>background:</c> child, before it is clamped to the idle
        /// timeout in the constructor (a poll longer than the timeout it serves would never fire in time).
        /// </summary>
        internal static readonly TimeSpan BackgroundChildPollInterval = TimeSpan.FromSeconds(30);

        private readonly Lock _gate = new();

        private readonly TimeSpan _idleTimeout;

        private readonly TimeSpan _backgroundPollInterval;

        private readonly TimeProvider _time;

        private readonly Action<HeldSession> _expired;

        private readonly ITimer _timer;

        // A GetTimestamp value, not a DateTimeOffset, so that a wall-clock jump neither expires nor keeps a session.
        private long _touched;

        // Set when the timer is gone. The session is still handed out until it is also ended.
        private bool _stopped;

        // Set once the session expired or the host closed it: its close has begun. Nothing may hand it out after that.
        private bool _ended;

        // The running turn OnIdle last saw holding the session past its idle timeout. Tracked so the completion
        // continuation that restarts the idle clock is attached once per turn, not once per poll.
        private Task? _turnHeartbeat;

        /// <summary>Holds one session and arms its idle timer.</summary>
        /// <param name="session">The session to hold.</param>
        /// <param name="idleTimeout">How long the session may stay untouched.</param>
        /// <param name="time">The clock the timer runs on.</param>
        /// <param name="expired">Called once, off the gate, when the session expired.</param>
        internal HeldSession(ConversationSession session, TimeSpan idleTimeout, TimeProvider time, Action<HeldSession> expired)
        {
            Session = session;
            _idleTimeout = idleTimeout;
            _backgroundPollInterval = idleTimeout < BackgroundChildPollInterval ? idleTimeout : BackgroundChildPollInterval;
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

        /// <summary>
        /// Restarts the idle clock, and stamps the workspace folder to match: every place that restarts one
        /// restarts the other, so "held" always implies "stamped within the idle timeout" to another server
        /// sharing the workspace root.
        /// </summary>
        /// <returns><see langword="false"/> once the session has ended. The caller must not hand it out then.</returns>
        internal bool TryTouch()
        {
            bool restarted = false;

            lock (_gate)
            {
                if (_ended)
                {
                    return false;
                }

                if (!_stopped)
                {
                    _touched = _time.GetTimestamp();
                    _ = _timer.Change(NextDueLocked(), Timeout.InfiniteTimeSpan);
                    restarted = true;
                }
            }

            if (restarted)
            {
                Session.Lifetime.TouchWorkspace();
            }

            return true;
        }

        /// <summary>Gets whether the session's close has begun, from its idle timer or from the host.</summary>
        internal bool IsEnded
        {
            get
            {
                lock (_gate)
                {
                    return _ended;
                }
            }
        }

        /// <summary>Ends the session because the host closes it. Its timer never fires after this.</summary>
        /// <returns>
        /// <see langword="false"/> when the session had already ended, and whoever ended it runs the close.
        /// </returns>
        internal bool TryEnd()
        {
            lock (_gate)
            {
                if (_ended)
                {
                    return false;
                }

                _ended = true;
                StopLocked();
                return true;
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
            Task? turn = null;
            Task? attachHeartbeatTo = null;
            bool stopped = false;
            bool rescheduled = false;
            bool touchWorkspace = false;

            lock (_gate)
            {
                if (_stopped)
                {
                    stopped = true;
                }
                else
                {
                    bool pollBackground = Session.Lifetime.MayHaveBackgroundChildren;

                    // A touch can re-arm the timer after it came due but before this callback ran.
                    TimeSpan idle = _time.GetElapsedTime(_touched);
                    if (idle < _idleTimeout)
                    {
                        // A background-capable document is watched the whole window through, at the shorter
                        // cadence, rather than only once the window is up: a child running right now is
                        // activity, read exactly like a touch, so the window does not close under it later for
                        // having missed it.
                        if (pollBackground && Session.Lifetime.HasRunningBackgroundChild())
                        {
                            _touched = _time.GetTimestamp();
                            touchWorkspace = true;
                        }

                        _ = _timer.Change(pollBackground ? _backgroundPollInterval : _idleTimeout - idle, Timeout.InfiniteTimeSpan);
                        rescheduled = true;
                    }
                    else
                    {
                        turn = Session.Cuts.RunningTurn();
                        if (turn is null)
                        {
                            _turnHeartbeat = null;

                            if (pollBackground && Session.Lifetime.HasRunningBackgroundChild())
                            {
                                // Still going: treat it as the touch it is, and keep polling at the shorter
                                // cadence rather than sleeping the full idle timeout again.
                                _touched = _time.GetTimestamp();
                                touchWorkspace = true;
                                rescheduled = true;
                                _ = _timer.Change(_backgroundPollInterval, Timeout.InfiniteTimeSpan);
                            }
                            else
                            {
                                _ended = true;
                                StopLocked();
                            }
                        }
                        else
                        {
                            // The turn alone has held the session past its idle timeout. Stamped now, and
                            // again at about half the idle timeout for as long as it keeps running, so a turn
                            // that runs on well past this point is never mistaken for dead by another server
                            // sharing the workspace root. The completion continuation below — which restarts
                            // the idle clock the moment the turn frees the slot — is attached once per turn,
                            // not once per poll, so a long turn does not stack a callback on every tick.
                            touchWorkspace = true;

                            if (!ReferenceEquals(_turnHeartbeat, turn))
                            {
                                _turnHeartbeat = turn;
                                attachHeartbeatTo = turn;
                            }

                            rescheduled = true;
                            _ = _timer.Change(_idleTimeout / 2, Timeout.InfiniteTimeSpan);
                        }
                    }
                }
            }

            if (stopped)
            {
                return;
            }

            // The workspace stamp is disk I/O; it runs after the gate is released so it never holds up a
            // concurrent TryTouch, IsEnded, or TryEnd call on the same session.
            if (touchWorkspace)
            {
                Session.Lifetime.TouchWorkspace();
            }

            // The idle clock restarts when the turn frees the slot. Closing now would wait on the turn anyway.
            _ = (attachHeartbeatTo?.ContinueWith(
                static (_, state) => ((HeldSession)state!).TryTouch(),
                this,
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default));

            if (rescheduled)
            {
                return;
            }

            if (turn is null)
            {
                _expired(this);
            }
        }

        /// <summary>
        /// How long the timer should sleep from a fresh touch: the poll cadence for a document that may have
        /// background children, or the full idle timeout for one that cannot.
        /// </summary>
        private TimeSpan NextDueLocked()
        {
            return Session.Lifetime.MayHaveBackgroundChildren ? _backgroundPollInterval : _idleTimeout;
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
            // would then run an idle timeout later inside that request's trace and logging scopes.
            bool restoreFlow = false;
            try
            {
                if (!ExecutionContext.IsFlowSuppressed())
                {
                    _ = ExecutionContext.SuppressFlow();
                    restoreFlow = true;
                }

                return _time.CreateTimer(
                    static state => ((HeldSession)state!).OnIdle(), this, NextDueLocked(), Timeout.InfiniteTimeSpan);
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
