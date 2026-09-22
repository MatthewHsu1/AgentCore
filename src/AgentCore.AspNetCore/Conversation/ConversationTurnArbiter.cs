using AgentCore.Application.Ports;
using Microsoft.Extensions.AI;

namespace AgentCore.AspNetCore.Conversation
{
    /// <summary>
    /// Decides which turn of one conversation may speak, and which one a barge-in cuts short.
    /// </summary>
    /// <param name="initialSession"></param>
    /// <param name="output">Where a turn's reply goes, one fragment at a time.</param>
    /// <param name="observer">Watches a turn's task, and never lets its fault go unobserved.</param>
    /// <param name="logPromptHeld">
    /// Logs that one prompt was held until the turn in flight ends, given the id of the conversation it was
    /// held for. Called once per held prompt, from inside the turn lock.
    /// </param>
    /// <param name="logPendingPromptDropped">
    /// Logs that a further prompt was dropped because one was already held, given the id of the conversation
    /// it was dropped for. Called at most once per connection, from inside the turn lock.
    /// </param>
    /// <param name="connectionToken">
    /// Cancelled once, for any reason the transport is going away. Every turn reads it, and no
    /// held prompt starts a turn after it fires.
    /// </param>
    internal sealed class ConversationTurnArbiter(
        IConversationPort initialSession,
        IConversationOutputPort output,
        ConnectionTaskObserver observer,
        Action<string> logPromptHeld,
        Action<string> logPendingPromptDropped,
        CancellationToken connectionToken)
    {
        private volatile IConversationPort _session = initialSession;

        private readonly IConversationOutputPort _output = output;

        private readonly ConnectionTaskObserver _observer = observer;

        private readonly Action<string> _logPromptHeld = logPromptHeld;

        private readonly Action<string> _logPendingPromptDropped = logPendingPromptDropped;

        private readonly CancellationToken _connectionToken = connectionToken;

        private Task _turn = Task.CompletedTask;

        private bool _loggedPendingPromptDropped;

        private readonly Lock _turnLock = new();

        private long _turnId;

        private long _interruptedTurnId;

        private long _spokenTurnId;

        private bool _turnActive;

        private (string Text, ConversationTurnClock? Clock)? _pendingPrompt;

        /// <summary>What a voice caller hears when a tool waits on an approval the conversation cannot take.</summary>
        internal const string PendingApprovalNotice =
            "That action needs a human approval, which this conversation can't take.";

        /// <summary>Gets the turn running now, or the one that ran last.</summary>
        public Task CurrentTurn
        {
            get
            {
                lock (_turnLock)
                {
                    return _turn;
                }
            }
        }

        /// <summary>Points this arbiter at another conversation, keeping every turn it is already running.</summary>
        /// <param name="session">The conversation every turn started from now on runs against.</param>
        public void Rebind(IConversationPort session)
        {
            _session = session;
        }

        /// <summary>Starts a turn for what the caller just said, or holds it until the turn in flight ends.</summary>
        /// <param name="text">What the caller said.</param>
        /// <param name="clock">
        /// Times this turn from the arrival of the words, or <see langword="null"/> to measure nothing.
        /// It follows the prompt through the hold, so a held prompt is still measured from when the
        /// caller stopped speaking rather than from when the turn in flight let go.
        /// </param>
        /// <returns>
        /// The turn this conversation started, or a completed task when the prompt was held or dropped. It
        /// reports only completion: a turn's own fault stays on <see cref="CurrentTurn"/>, which the
        /// next turn and teardown both already observe through <see cref="ConnectionTaskObserver"/>.
        /// </returns>
        public Task StartTurnAsync(string text, ConversationTurnClock? clock = null)
        {
            IConversationPort session = _session;

            Task justFinished;
            lock (_turnLock)
            {
                if (_turnActive)
                {
                    if (_pendingPrompt is null)
                    {
                        _pendingPrompt = (text, clock);

                        _logPromptHeld(session.ConversationId);
                    }
                    else if (!_loggedPendingPromptDropped)
                    {
                        _loggedPendingPromptDropped = true;

                        _logPendingPromptDropped(session.ConversationId);
                    }

                    return Task.CompletedTask;
                }

                justFinished = _turn;
                _turnActive = true;
            }

            return StartAfterAsync(session, text, clock, justFinished);
        }

        /// <summary>Ends the running turn where the caller cut the reply off, and stops the audio behind it.</summary>
        /// <param name="heardText">The text the caller actually heard, as the transport reported it.</param>
        /// <param name="playedDuration">How much of the reply played, as the transport reported it.</param>
        /// <returns>
        /// <see langword="true"/> when the conversation recorded the barge-in, against the running turn or
        /// against the turn that finished last, and <see langword="false"/> when there was nothing to
        /// record it against.
        /// </returns>
        public bool Interrupt(string heardText, TimeSpan playedDuration)
        {
            IConversationPort session = _session;

            bool cutsRunningTurn;
            lock (_turnLock)
            {
                long spoken = Interlocked.Read(ref _spokenTurnId);

                if (spoken != 0)
                {
                    _ = Interlocked.Exchange(ref _interruptedTurnId, spoken);
                }

                cutsRunningTurn = spoken != 0 && spoken == Interlocked.Read(ref _turnId);
            }

            bool recorded = session.Interrupt(heardText, playedDuration, cutsRunningTurn);

            _ = _observer.ObserveAsync(_output.StopAsync(_connectionToken).AsTask(), ConnectionTaskKind.WriteLoop);

            return recorded;
        }

        /// <summary>Observes the turn that just ended, then starts the next one and follows it to its end.</summary>
        /// <param name="session">The conversation the turn this starts runs against.</param>
        /// <param name="text">What the caller said.</param>
        /// <param name="clock">Times the turn this starts, or <see langword="null"/> to measure nothing.</param>
        /// <param name="justFinished">The turn this conversation read out of <see cref="CurrentTurn"/>.</param>
        /// <returns>A task that completes when the turn this conversation starts completes.</returns>
        private async Task StartAfterAsync(
            IConversationPort session,
            string text,
            ConversationTurnClock? clock,
            Task justFinished)
        {
            await _observer.ObserveAsync(justFinished, ConnectionTaskKind.Turn).ConfigureAwait(false);

            Task started;
            lock (_turnLock)
            {
                if (_connectionToken.IsCancellationRequested)
                {
                    _turnActive = false;
                    return;
                }

                long turnId = Interlocked.Increment(ref _turnId);
                started = _turn = RunTurnAsync(session, text, turnId, clock);
            }

            try
            {
                await started.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Swallowed here on purpose, and only here. The task this method returns exists so a
                // caller can follow the turn to its end; the fault itself stays on CurrentTurn, which
                // the next turn's own observation above and teardown's both already report through
                // ConnectionTaskObserver. Reporting it a second time here would log one fault twice.
            }
        }

        /// <summary>Runs one turn end to end, and streams its reply to the output port.</summary>
        /// <param name="session">
        /// The conversation this turn runs against. Passed in rather than read off the field, so a turn already
        /// in flight when <see cref="Rebind"/> names another conversation still finishes on the one it started on.
        /// </param>
        /// <param name="text">What the caller said.</param>
        /// <param name="turnId">The id this turn's output is marked with.</param>
        /// <param name="clock">Times this turn, or <see langword="null"/> to measure nothing.</param>
        /// <returns>A task that completes when the turn has ended, however it ended.</returns>
        private async Task RunTurnAsync(
            IConversationPort session,
            string text,
            long turnId,
            ConversationTurnClock? clock)
        {
            try
            {
                await foreach (ChatResponseUpdate? update in session
                    .RunTurnStreamingAsync(text, _connectionToken)
                    .ConfigureAwait(false))
                {
                    clock?.MarkFirstToken();

                    if (Interlocked.Read(ref _interruptedTurnId) == turnId)
                    {
                        continue;
                    }

                    if (update.Text is { Length: > 0 } piece)
                    {
                        _ = Interlocked.Exchange(ref _spokenTurnId, turnId);

                        clock?.MarkFirstSpeech();

                        await _output.SpeakAsync(piece, _connectionToken).ConfigureAwait(false);
                    }
                }

                if (Interlocked.Read(ref _interruptedTurnId) != turnId)
                {
                    if (session.LastTurn?.Approvals is { Count: > 0 })
                    {
                        await _output.SpeakAsync(PendingApprovalNotice, _connectionToken).ConfigureAwait(false);
                    }

                    await _output.CompleteAsync(_connectionToken).ConfigureAwait(false);

                    clock?.MarkReplyEnd();
                }
            }
            catch (OperationCanceledException)
            {

            }
            finally
            {
                RunPendingPrompt();
            }
        }

        /// <summary>Starts the one prompt <see cref="StartTurnAsync"/> held while this turn ran.</summary>
        private void RunPendingPrompt()
        {
            lock (_turnLock)
            {
                if (_pendingPrompt is not { } held || _connectionToken.IsCancellationRequested)
                {
                    _turnActive = false;
                    return;
                }

                _pendingPrompt = null;

                long turnId = Interlocked.Increment(ref _turnId);

                _turn = RunTurnAsync(_session, held.Text, turnId, held.Clock);
            }
        }
    }
}
