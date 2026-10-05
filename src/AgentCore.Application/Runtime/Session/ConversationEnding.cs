using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Domain.Audit;

namespace AgentCore.Application.Runtime.Session
{
    /// <summary>
    /// Raises <see cref="ConversationEnded"/> once, after the turn that was running when the end was asked for.
    /// It never cancels that turn: a tool may end the conversation from inside it.
    /// </summary>
    internal sealed class ConversationEnding(ConversationSession session) : IDisposable
    {
        /// <summary>How long an end waits for the running turn to seal.</summary>
        internal static readonly TimeSpan TurnWait = TimeSpan.FromSeconds(5);

        /// <summary>
        /// How long a tool still running when the conversation ends, or its session is disposed at shutdown, may go on
        /// before it is cancelled. A cut or a withdraw never cancels a running tool; this bounds only
        /// the end. Shell tasks take about 40 s, so this is not LiveKit's 5 s.
        /// </summary>
        internal static readonly TimeSpan ToolGrace = TimeSpan.FromSeconds(30);

        private readonly CancellationTokenSource _toolStop = new();

        private readonly TaskCompletionSource _toolsStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly Lock _toolGate = new();

        private ITimer? _toolStopTimer;

        private bool _disposed;

        // Guarded by the session's turn lock, as the turn slot is: an end waits only while its turn holds the slot,
        // and the turn that frees the slot raises it in the same step, before anything waiting on that turn resumes.
        private (ConversationEndReason Reason, int Turn)? _pending;

        private readonly TaskCompletionSource _whenRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly Lock _callGate = new();

        private int _requested;

        private CallMark? _call;

        private bool _callLeft;

        private EndAsk? _ask;

        // Guarded by the session's turn lock, as Raise is.
        private bool _raised;

        private readonly List<HookNotice> _afterEnd = [];

        /// <summary>
        /// Gets whether the end was asked for, by the host, a tool, or a terminal stage. It is set at once, while
        /// <see cref="ConversationEnded"/> may still wait for the running turn, so refusals and cuts read this.
        /// </summary>
        internal bool Requested => Volatile.Read(ref _requested) == 1;

        /// <summary>Gets a task that completes once <see cref="Requested"/> is set, however the end was asked for.</summary>
        internal Task WhenRequested => _whenRequested.Task;

        /// <summary>Gets the token every running tool of this conversation reads. It is cancelled <see cref="ToolGrace"/> after the end.</summary>
        internal CancellationToken ToolStop => _toolStop.Token;

        /// <summary>Gets a task that completes once the backstop cancelled <see cref="ToolStop"/>.</summary>
        internal Task WhenToolsStopped => _toolsStopped.Task;

        /// <summary>Starts the <see cref="ToolGrace"/> of the tools still running. Only the first call starts it.</summary>
        internal void StopToolsAfterGrace()
        {
            lock (_toolGate)
            {
                if (_toolStopTimer is null && !_disposed)
                {
                    _toolStopTimer = session.Time.CreateTimer(static state => ((ConversationEnding)state!).StopTools(), this, ToolGrace, Timeout.InfiniteTimeSpan);
                }
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            lock (_toolGate)
            {
                _disposed = true;
                _toolStopTimer?.Dispose();
            }

            _toolStop.Dispose();
        }

        // The timer can fire while the session is disposed; a disposed source has no tool left to stop.
        private void StopTools()
        {
            try
            {
                _toolStop.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                _ = _toolsStopped.TrySetResult();
            }
        }

        /// <summary>Gets whether a phone call holds this session, taken by the transport or only admitted.</summary>
        internal bool HasCall => Volatile.Read(ref _call) is not null;

        /// <summary>
        /// Holds this session for one admitted call, so no second call joins it (one live call per conversation). A
        /// newer call with the id of the call that holds the session takes it over, as LiveKit lets the newest
        /// connection of one identity stay: the older call's <paramref name="holder"/> is run, after the claim.
        /// </summary>
        /// <param name="callId">The transport's id of the call.</param>
        /// <param name="holder">Names the claiming call, and is run if a newer call with its id takes the session over.</param>
        /// <param name="keptStart">When the call that was taken over started, so its length runs on; otherwise <see langword="null"/>.</param>
        /// <returns>
        /// <see langword="false"/> when a call with another id holds the session, or when the conversation is leaving:
        /// its call let go of it, or its end was asked for.
        /// </returns>
        internal bool ClaimCall(string callId, Action holder, out DateTimeOffset? keptStart)
        {
            ArgumentException.ThrowIfNullOrEmpty(callId);
            ArgumentNullException.ThrowIfNull(holder);
            CallMark? replaced;
            lock (_callGate)
            {
                replaced = _call;
                if (replaced is not null && (replaced.CallId != callId || _callLeft || Requested))
                {
                    keptStart = null;
                    return false;
                }

                _call = new CallMark(callId, replaced?.StartedAt, holder);
            }

            keptStart = replaced?.StartedAt;
            replaced?.Holder();
            return true;
        }

        /// <summary>
        /// Marks this session as carrying a phone call, so every <see cref="ConversationEnded"/> it raises names the
        /// call. Only a session no call holds, or one this same call holds, takes the mark.
        /// </summary>
        /// <param name="callId">The transport's id of the call.</param>
        /// <param name="holder">Names the call, as in <see cref="ClaimCall"/>.</param>
        /// <param name="startedAt">When the transport took the call, on the session's clock.</param>
        /// <returns><see langword="false"/> when another call holds the session; its mark is left as it was.</returns>
        internal bool TryMarkCall(string callId, Action holder, DateTimeOffset startedAt)
        {
            ArgumentException.ThrowIfNullOrEmpty(callId);
            ArgumentNullException.ThrowIfNull(holder);
            lock (_callGate)
            {
                if (_call is { } held && !ReferenceEquals(held.Holder, holder))
                {
                    return false;
                }

                _call = new CallMark(callId, startedAt, holder);
                return true;
            }
        }

        /// <summary>
        /// The call that holds this session lets go of it, to end or close it: from now on no newer call takes it
        /// over. Calling it again is harmless.
        /// </summary>
        /// <param name="holder">Names the call, as in <see cref="ClaimCall"/>.</param>
        /// <returns>
        /// <see langword="false"/> when a newer call took the session over from this one: the session, its end and its
        /// brief are that call's now.
        /// </returns>
        internal bool ReleaseCall(Action holder)
        {
            lock (_callGate)
            {
                if (_call is { } held && !ReferenceEquals(held.Holder, holder))
                {
                    return false;
                }

                _callLeft = true;
                return true;
            }
        }

        /// <summary>Ends the conversation now, or right after the running turn. Only the first end counts.</summary>
        /// <param name="reason">Why the conversation ended.</param>
        /// <param name="cause">The transport's word for why its call ended, or <see langword="null"/>. Kept only for the first end.</param>
        /// <returns><see langword="true"/> when this call is the first end of the session.</returns>
        internal bool Request(ConversationEndReason reason, string? cause = null)
        {
            if (Interlocked.Exchange(ref _requested, 1) == 1)
            {
                return false;
            }

            _ = _whenRequested.TrySetResult();
            StopToolsAfterGrace();

            Volatile.Write(ref _ask, new EndAsk(cause, session.Time.GetUtcNow()));

            bool waits = false;
            lock (session.TurnLock)
            {
                if (session.Cuts.RunningTurnIndex() is int running)
                {
                    _pending = (reason, running);
                    waits = true;
                }
            }

            if (waits)
            {
                _ = RaiseAfterWaitAsync();
                return true;
            }

            Raise(reason, terminalStage: null, lastAdmittedTurn: session.State.TurnIndex - 1);
            return true;
        }

        /// <summary>
        /// Raises a notice that must follow <see cref="ConversationEnded"/>: at once when no end is waiting to be raised,
        /// or right after that end when one is (it may wait on the running turn).
        /// </summary>
        /// <param name="notice">The notice, a call's <see cref="CallEnded"/>.</param>
        internal void RaiseAfterEnd(HookNotice notice)
        {
            lock (session.TurnLock)
            {
                if (Requested && !_raised)
                {
                    _afterEnd.Add(notice);
                    return;
                }

                _ = session.Hooks.Raise(notice);
            }
        }

        /// <summary>
        /// Marks the end of a conversation an earlier session already ended, as the state restored says. Its
        /// <see cref="ConversationEnded"/> is in the chain already, so nothing is raised, and a notice
        /// that must follow it goes out at once.
        /// </summary>
        internal void Restored()
        {
            lock (session.TurnLock)
            {
                Volatile.Write(ref _requested, 1);
                _raised = true;
            }

            _ = _whenRequested.TrySetResult();
        }

        /// <summary>Called by the seal after the turn's <see cref="TurnCompleted"/>: raises a pending end, or the terminal one.</summary>
        internal void AfterTurn(int turnIndex, string stageAfter, bool complete)
        {
            if (TakePending() is { } pending)
            {
                Raise(pending.Reason, terminalStage: null, turnIndex);
            }
            else if (complete && Interlocked.Exchange(ref _requested, 1) == 0)
            {
                _ = _whenRequested.TrySetResult();
                Raise(ConversationEndReason.AgentCompleted, stageAfter, turnIndex);
            }
        }

        /// <summary>
        /// Raises an end still waiting on its turn. The cut tracker calls this as the turn frees its slot, under the
        /// turn lock: a turn the store refused, or one dropped unread, never reaches <see cref="AfterTurn"/>.
        /// </summary>
        internal void RaisePending()
        {
            lock (session.TurnLock)
            {
                if (TakePending() is { } pending)
                {
                    Raise(pending.Reason, terminalStage: null, pending.Turn);
                }
            }
        }

        private async Task RaiseAfterWaitAsync()
        {
            await Task.Delay(TurnWait, session.Time).ConfigureAwait(false);
            RaisePending();
        }

        private (ConversationEndReason Reason, int Turn)? TakePending()
        {
            lock (session.TurnLock)
            {
                (ConversationEndReason Reason, int Turn)? pending = _pending;
                _pending = null;
                return pending;
            }
        }

        private void Raise(ConversationEndReason reason, string? terminalStage, int lastAdmittedTurn)
        {
            SessionHooks hooks = session.Hooks;
            lock (session.TurnLock)
            {
                hooks.EnsureStarted();
                _ = hooks.RaiseEnd(
                    new ConversationEnded(hooks.Scope(turnIndex: null, terminalStage), reason, terminalStage, Call: CallEndNow()),
                    lastAdmittedTurn);
                _raised = true;
                foreach (HookNotice after in _afterEnd)
                {
                    _ = hooks.Raise(after);
                }

                _afterEnd.Clear();
            }
        }

        // A call's length runs to when its end was asked for: the raise may wait up to TurnWait on the running turn.
        private CallEnd? CallEndNow()
        {
            if (Volatile.Read(ref _call) is not { StartedAt: { } startedAt } call)
            {
                return null;
            }

            EndAsk? ask = Volatile.Read(ref _ask);
            DateTimeOffset endedAt = ask?.At ?? session.Time.GetUtcNow();
            return new CallEnd(call.CallId, (endedAt - startedAt).TotalSeconds, ask?.Cause);
        }

        private sealed record CallMark(string CallId, DateTimeOffset? StartedAt, Action Holder);

        private sealed record EndAsk(string? Cause, DateTimeOffset At);
    }
}
