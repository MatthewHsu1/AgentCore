// Portions derived from LiveKit Agents, livekit-agents/livekit/agents/voice/agent_session.py:1530-1562
// (say), 1634-1651 (interrupt), 1741-1764 (wait_for_idle), 1159-1160 (start), 1349-1386
// (_teardown_activity, no AgentTask/AMD/audio part), 2020-2044 (away timer), 2120-2181
// (_update_agent_state, state and away-timer parts), 2183-2244 (_update_user_state, state and
// away-timer parts), 2254-2267 (_user_input_transcribed); livekit-agents/livekit/agents/voice/
// agent_activity.py: 1692-1766 (say, text branch 1751-1765), 2070-2133 (wait_for_idle, wait_for_agent
// branch), 3086-3098 (_no_pending_speech, _on_pipeline_reply_done); livekit-agents/livekit/agents/
// voice/turn.py: 190-198 (_INTERRUPTION_DEFAULTS["enabled"]); speech_handle.py:349
// (_user_silence_event.set()), commit d8405f132e1bd960f298190c18daf81ffc1faf45. Copyright 2023
// LiveKit, Inc. Licensed under the Apache License, Version 2.0. Modified: translated to C#; one
// activity per conversation, so AgentSession and AgentActivity collapse into one type; no handoff, no
// drain, no realtime branch; what "away" says is ours, not LiveKit's (plan Q2/owner ruling); input-side
// wiring is UserTurnHandler.cs.

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.Voice
{
    /// <summary>One voice conversation's speech: what the agent says, and when.</summary>
    [SuppressMessage(
        "Design",
        "CA1001:Types that own disposable fields should be disposable",
        Justification = "The one disposable field is a CancellationTokenSource with no timer and no linked "
            + "parent, so it holds nothing to release; CloseAsync cancels it.")]
    internal sealed class VoiceSession
    {
        /// <summary>LiveKit's default (<c>turn.py</c> <c>_INTERRUPTION_DEFAULTS["enabled"]</c>). The only
        /// override is answering-machine detection, which is out of scope (audio-only).</summary>
        internal const bool DefaultAllowInterruptions = true;

        private readonly AsyncEvent _userSilence = new();

        private readonly CancellationTokenSource _closedCancellation = new();

        private readonly UserAwayTimer _userAwayTimer;

        private AgentState _agentState = AgentState.Listening;

        private UserState _userState = UserState.Listening;

        /// <param name="output">Where every reply goes.</param>
        /// <param name="time">The clock the interruption backstop and the queue wait run on.</param>
        /// <param name="logger">Where scheduling warnings and speech task faults are reported.</param>
        /// <param name="userAway">
        /// When and what to say once the caller has gone quiet, or <see langword="null"/> to disable the
        /// timer (plan Q2/owner ruling). The config/schema surface for this is plan step 8.
        /// </param>
        public VoiceSession(IConversationOutputPort output, TimeProvider time, ILogger logger, UserAwayOptions? userAway = null)
        {
            Output = output;
            Time = time;
            Logger = logger;
            _userAwayTimer = new UserAwayTimer(time, userAway, OnUserAwayTimer);
            Scheduler = new SpeechScheduler(SessionLock, logger);
            _userSilence.Set();
            Scheduler.ResumeScheduling();
        }

        /// <summary>Raised each time <see cref="AgentState"/> actually changes.</summary>
        public event Action<AgentStateChanged>? AgentStateChanged;

        /// <summary>Raised each time <see cref="UserState"/> actually changes.</summary>
        public event Action<UserStateChanged>? UserStateChanged;

        /// <summary>Gets what the agent is doing right now.</summary>
        public AgentState AgentState
        {
            get
            {
                lock (SessionLock)
                {
                    return _agentState;
                }
            }
        }

        /// <summary>Gets what the caller is doing right now.</summary>
        public UserState UserState
        {
            get
            {
                lock (SessionLock)
                {
                    return _userState;
                }
            }
        }

        /// <summary>Gets whether a speech is waiting on its own tools with its words already spoken.</summary>
        internal bool HasBackgroundSpeeches => Scheduler.HasBackgroundSpeeches;

        /// <summary>Gets the scheduler that plays this session's speeches.</summary>
        internal SpeechScheduler Scheduler { get; }

        /// <summary>Gets the lock every state read-modify-write of this session is made under.</summary>
        internal Lock SessionLock { get; } = new();

        /// <summary>Gets where every speech's text goes.</summary>
        internal IConversationOutputPort Output { get; }

        /// <summary>Gets the clock the session runs on.</summary>
        internal TimeProvider Time { get; }

        /// <summary>Gets where the session's speeches report.</summary>
        internal ILogger Logger { get; }

        /// <summary>Speaks fixed text: a notice, a filler, or anything not generated by a model step.</summary>
        /// <param name="text">The text to speak. Never empty.</param>
        /// <param name="allowInterruptions">
        /// Whether a committed caller turn or a barge-in may cut this speech. Defaults to
        /// <see cref="DefaultAllowInterruptions"/>.
        /// </param>
        /// <returns>A handle to the speech, done once it has been spoken or dropped.</returns>
        public SpeechHandle Say(string text, bool? allowInterruptions = null)
        {
            SpeechHandle handle = SpeechHandle.Create(Time, Logger, allowInterruptions ?? DefaultAllowInterruptions);
            _ = Scheduler.CreateSpeechTask(
                () => SayReply.RunAsync(handle, this, Output, SingleFragment(text), handle.TaskCancellationToken),
                handle);

            // Registered on the handle, not on the task: it must run once every task tied to the speech
            // has finished and the speech itself is marked done (agent_activity.py's _on_pipeline_reply_done
            // relies on the same ordering, guaranteed there by asyncio's done-callback registration order).
            handle.AddDoneCallback(_ => OnSpeechDone());
            Scheduler.ScheduleSpeech(handle, SpeechPriority.Normal);
            return handle;
        }

        /// <summary>Interrupts the current speech, the background speeches, and the queue up to the first
        /// speech that disallows interruptions.</summary>
        /// <param name="force">Interrupt even a speech that disallows interruptions.</param>
        /// <returns>A task that completes once every speech this interrupted is done.</returns>
        public Task Interrupt(bool force = false)
        {
            return Scheduler.Interrupt(force);
        }

        /// <summary>Waits until no speech is current or queued.</summary>
        /// <param name="cancellationToken">Cancels this wait only, never a speech.</param>
        /// <exception cref="VoiceSessionClosedException">The session closed while this call waited.</exception>
        public async Task WaitForIdleAsync(CancellationToken cancellationToken = default)
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _closedCancellation.Token);

            try
            {
                await Scheduler.WaitForIdleAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_closedCancellation.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                throw new VoiceSessionClosedException();
            }

            // The scheduler can settle to idle and close race one another; recheck once more before
            // returning, as LiveKit's wait_for_idle does right after its own loop exits.
            if (_closedCancellation.IsCancellationRequested)
            {
                throw new VoiceSessionClosedException();
            }
        }

        /// <summary>LiveKit's session start (<c>agent_session.py:1159-1160</c>): the agent starts listening, which
        /// arms the away window.</summary>
        internal void Start()
        {
            lock (SessionLock)
            {
                if (_agentState == AgentState.Listening && _userState == UserState.Listening)
                {
                    ArmUserAwayTimerLocked();
                }
            }
        }

        /// <summary>LiveKit's <c>_teardown_activity</c> without drain (<c>agent_session.py:1349-1386</c>): close,
        /// force-interrupt every speech, stop scheduling once every speech task has ended, and wait out the
        /// current speech.</summary>
        /// <returns>A task that completes once no speech of this session is left running.</returns>
        internal async Task CloseAsync()
        {
            MarkClosed();

            await Scheduler.Interrupt(force: true).ConfigureAwait(false);
            await Scheduler.PauseSchedulingAsync().ConfigureAwait(false);

            if (Scheduler.CurrentSpeech is { } current)
            {
                _ = await current;
            }

            // agent_session.py:1329: the teardown above hands the floor back, and a hand-back arms the timer.
            lock (SessionLock)
            {
                _userAwayTimer.Disarm();
            }
        }

        /// <summary>Marks the session closed. Every current and future <see cref="WaitForIdleAsync"/> call
        /// raises <see cref="VoiceSessionClosedException"/>, and the away timer stops.</summary>
        internal void MarkClosed()
        {
            _closedCancellation.Cancel();
            lock (SessionLock)
            {
                _userAwayTimer.Disarm();
            }
        }

        /// <summary>Sets the agent state, and raises <see cref="AgentStateChanged"/> if it actually changed.</summary>
        internal void SetAgentState(AgentState state)
        {
            AgentStateChanged change;
            lock (SessionLock)
            {
                if (_agentState == state)
                {
                    return;
                }

                change = new AgentStateChanged(_agentState, state, Time.GetUtcNow());
                _agentState = state;

                if (state == AgentState.Listening && _userState == UserState.Listening)
                {
                    ArmUserAwayTimerLocked();
                }
                else
                {
                    _userAwayTimer.Disarm();
                }
            }

            AgentStateChanged?.Invoke(change);
        }

        /// <summary>Sets the user state, and raises <see cref="UserStateChanged"/> if it actually changed.</summary>
        internal void SetUserState(UserState state)
        {
            UserStateChanged? change;
            lock (SessionLock)
            {
                change = SetUserStateLocked(state);
            }

            if (change is not null)
            {
                UserStateChanged?.Invoke(change);
            }
        }

        /// <summary>Waits until no interim prompt is in flight.</summary>
        internal Task WaitForUserSilenceAsync(CancellationToken cancellationToken = default)
        {
            return _userSilence.WaitAsync(cancellationToken);
        }

        /// <summary>Marks the caller as mid-utterance: <see cref="UserTurnHandler.OnInterimTranscript"/>'s
        /// <c>on_start_of_speech</c> half (<c>agent_activity.py:2392</c>).</summary>
        internal void ClearUserSilence()
        {
            _userSilence.Clear();
        }

        /// <summary>Marks the caller as not mid-utterance: <see cref="UserTurnHandler.OnFinalTranscript"/>'s
        /// <c>on_end_of_speech</c> half (<c>agent_activity.py:2433</c>).</summary>
        internal void MarkUserSilent()
        {
            _userSilence.Set();
        }

        /// <summary>LiveKit's <c>_user_input_transcribed</c> (<c>agent_session.py:2254-2267</c>), the away-timer
        /// part: a final transcript resets the away window while listening, or cancels a stray "away".</summary>
        internal void RefreshUserAwayOnFinalTranscript()
        {
            bool backToListening;
            lock (SessionLock)
            {
                if (_userState == UserState.Speaking)
                {
                    return;
                }

                backToListening = _userState == UserState.Away;
                if (!backToListening && _userState == UserState.Listening && _agentState == AgentState.Listening)
                {
                    ArmUserAwayTimerLocked();
                }
            }

            if (backToListening)
            {
                SetUserState(UserState.Listening);
            }
        }

        /// <summary>LiveKit's <c>_on_pipeline_reply_done</c>: once no speech is left, hand the floor back.</summary>
        internal void OnSpeechDone()
        {
            if (Scheduler.NoPendingSpeech)
            {
                SetAgentState(HasBackgroundSpeeches ? AgentState.Thinking : AgentState.Listening);
            }
        }

        /// <summary>Arms the away timer, unless the session is closed or a speech still waits on its own tools
        /// (LiveKit's <c>_RunningTasks</c> guard, <c>agent_session.py:2025-2027</c>). Called under <c>_lock</c>.</summary>
        private void ArmUserAwayTimerLocked()
        {
            if (Scheduler.HasBackgroundSpeeches || _closedCancellation.IsCancellationRequested)
            {
                _userAwayTimer.Disarm();
                return;
            }

            _userAwayTimer.Arm();
        }

        /// <summary>Sets the user state and arms or disarms the away timer to match. Called under <c>_lock</c>.</summary>
        /// <returns>The change to raise once the lock is released, or <see langword="null"/> if nothing changed.</returns>
        private UserStateChanged? SetUserStateLocked(UserState state)
        {
            if (_userState == state)
            {
                return null;
            }

            UserStateChanged change = new(_userState, state, Time.GetUtcNow());
            _userState = state;

            if (state == UserState.Listening && _agentState == AgentState.Listening)
            {
                ArmUserAwayTimerLocked();
            }
            else
            {
                _userAwayTimer.Disarm();
            }

            return change;
        }

        /// <summary>Runs on a timer thread, where a throw ends the process. Only the current arm of an open
        /// session acts; see <see cref="UserAwayTimer"/>.</summary>
        private void OnUserAwayTimer(long arm)
        {
            UserStateChanged? change;
            lock (SessionLock)
            {
                if (_closedCancellation.IsCancellationRequested || !_userAwayTimer.IsCurrent(arm))
                {
                    return;
                }

                change = SetUserStateLocked(UserState.Away);
            }

            if (change is not null)
            {
                UserStateChanged?.Invoke(change);
            }

            // LiveKit only emits user_state_changed and leaves "away" to the app; this is our own
            // answer to plan Q2, not a LiveKit behaviour.
            try
            {
                _ = Say(_userAwayTimer.Options!.Say);
            }
            catch (InvalidOperationException ex)
            {
                // A close can pause scheduling between the check above and this call.
                VoiceConversationLog.UserAwayPromptNotScheduled(Logger, ex);
            }
        }

        private static async IAsyncEnumerable<string> SingleFragment(string text)
        {
            yield return text;
        }
    }
}
