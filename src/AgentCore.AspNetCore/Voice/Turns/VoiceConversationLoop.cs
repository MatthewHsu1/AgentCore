using AgentCore.Application.Hooks.Gates;
using AgentCore.Application.Hooks.Layers;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Runtime.Session;
using AgentCore.AspNetCore.Calls;
using AgentCore.AspNetCore.Voice.Diagnostics;
using AgentCore.AspNetCore.Voice.Options;
using AgentCore.AspNetCore.Voice.Ports;
using AgentCore.AspNetCore.Voice.Session;
using AgentCore.AspNetCore.Voice.Speech.Replies;
using AgentCore.AspNetCore.Voice.Transport;
using AgentCore.Domain.Audit;
using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.Voice.Turns
{
    /// <summary>
    /// Feeds one voice conversation's inbound events to the turn-taking classes: user turns, replies,
    /// barge-ins, and the away prompt.
    /// </summary>
    /// <param name="calls">What every call of the host shares: the sessions, the hooks, the clock.</param>
    /// <param name="entry">The entry the URL named. Every open and lookup this loop makes names it.</param>
    /// <param name="transport">The vendor kind, which the call gate and <c>CallStarted</c> name.</param>
    /// <param name="output">Where every reply goes.</param>
    /// <param name="observer">Watches every task this loop starts.</param>
    /// <param name="time">The clock the host bound.</param>
    /// <param name="logger">The logger of the connection that owns this loop.</param>
    /// <param name="closeTimeout">How long a replaced session may take to close.</param>
    /// <param name="connectionToken">Cancelled once, for any reason the transport is going away.</param>
    /// <param name="options">The away prompt and the per-tool filler, or <see langword="null"/> for <see cref="VoiceOptions.Default"/>.</param>
    /// <param name="replaced">
    /// Run once another connection's call with this loop's call id took the conversation over, so the transport
    /// drops this connection. It runs on that call's admission, so it must not block.
    /// </param>
    internal sealed class VoiceConversationLoop(
        PhoneCallHost calls,
        string entry,
        string transport,
        IConversationOutputPort output,
        ConnectionTaskObserver observer,
        TimeProvider time,
        ILogger logger,
        TimeSpan closeTimeout,
        CancellationToken connectionToken,
        VoiceOptions? options = null,
        Action? replaced = null)
    {
        private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>(StringComparer.Ordinal);

        private readonly VoiceOptions _options = options ?? VoiceOptions.Default;

        private readonly VoiceSession _voice = new(output, time, logger, (options ?? VoiceOptions.Default).UserAway);

        private readonly Lock _linesGate = new();

        private Task _lines = Task.CompletedTask;

        private volatile PhoneCall? _call;

        private volatile PhoneCall? _handingOver;

        private volatile ConversationSession? _session;

        private volatile VoiceActivity? _activity;

        private UserTurnHandler? _userTurns;

        private bool _loggedPromptBeforeSetup;

        private bool _loggedSecondSetup;

        /// <summary>Gets the id of the conversation this loop answers, or <see langword="null"/> before it started.</summary>
        public string? ConversationId => _session?.ConversationId;

        /// <summary>Gets whether a conversation has started on this loop.</summary>
        public bool HasStarted => _session is not null;

        /// <summary>
        /// Gets whether the loop stopped because the call the transport offered was refused: by the call gate, or because
        /// it could not start on its conversation.
        /// </summary>
        public bool WasRefused { get; private set; }

        /// <summary>Gets why the call was refused, when <see cref="WasRefused"/> is set.</summary>
        public CallRefusal? Refusal { get; private set; }

        /// <summary>Gets whether the call was refused as busy because another call holds its conversation.</summary>
        internal bool HeldByCall { get; private set; }

        internal PhoneCall? Call => _call;

        /// <summary>Gets whether another connection's call with this loop's call id took the conversation over.</summary>
        internal bool WasReplaced => _call?.WasReplaced == true;

        /// <summary>Reads every inbound event, in order, until the stream ends or a call is refused.</summary>
        /// <param name="inputs">The conversation's inbound events.</param>
        /// <returns>A task that completes when the stream ends, or once <see cref="WasRefused"/> is set.</returns>
        public async Task RunAsync(IAsyncEnumerable<ConversationInput> inputs)
        {
            await foreach (ConversationInput input in inputs.ConfigureAwait(false))
            {
                switch (input)
                {
                    case ConversationInput.Started started:
                        if (!await StartConversationAsync(started).ConfigureAwait(false))
                        {
                            return;
                        }

                        break;

                    case ConversationInput.Utterance { IsFinal: true } utterance:
                        await OnFinalUtteranceAsync(utterance.Text).ConfigureAwait(false);
                        break;

                    case ConversationInput.Utterance { Text.Length: > 0 }:
                        _userTurns?.OnInterimTranscript();
                        break;

                    case ConversationInput.Barge barge:
                        Interrupt(barge);
                        break;

                    default:
                        // A keypress, or an empty interim transcript. Neither is part of turn-taking.
                        break;
                }
            }
        }

        /// <summary>
        /// Tears the conversation's speech down: LiveKit's session close order, then the engine turns behind it, then
        /// the spoken lines still queued, each with what the caller heard by now.
        /// </summary>
        /// <returns>
        /// A task that completes once no speech is left running, every engine turn has ended, and every line was raised
        /// or its wait passed the close timeout.
        /// </returns>
        public async Task DrainAsync()
        {
            await _voice.CloseAsync().ConfigureAwait(false);

            if (_activity is { } activity)
            {
                await activity.EngineRun.ConfigureAwait(false);
            }

            _activity?.CloseBargeWindows();
            Task lines;
            lock (_linesGate)
            {
                lines = _lines;
            }

            await observer.ObserveAsync(lines, ConnectionTaskKind.SessionClose, closeTimeout).ConfigureAwait(false);
        }

        /// <summary>Ends the call, if one started, and closes its session.</summary>
        /// <param name="reason">Why the conversation ended.</param>
        /// <param name="cause">The transport's word for why, or <see langword="null"/>.</param>
        /// <returns>A task that completes once the session is closed or its close timed out.</returns>
        public async Task EndAsync(ConversationEndReason reason, string? cause = null)
        {
            if (_call is not { } call)
            {
                return;
            }

            await call.EndAsync(reason, cause).ConfigureAwait(false);
            await observer.ObserveAsync(call.CloseAsync().AsTask(), ConnectionTaskKind.SessionClose, closeTimeout).ConfigureAwait(false);
        }

        /// <returns><see langword="false"/> when the call was refused, so this loop holds no conversation.</returns>
        private async Task<bool> StartConversationAsync(ConversationInput.Started started)
        {
            PhoneCall? previous = _call;
            if (previous is not null)
            {
                if (!_loggedSecondSetup)
                {
                    _loggedSecondSetup = true;
                    VoiceConversationLog.SecondSetupFrame(logger, previous.ConversationId);
                }

                // The same call id: the new admission takes the call over and the conversation goes on, as a newer
                // connection would. Another id: the previous call's session closes with no end.
                if (previous.CallId == started.ConversationId)
                {
                    _handingOver = previous;
                }
                else
                {
                    await CloseReplacedAsync(previous).ConfigureAwait(false);
                    previous = null;
                }
            }

            CallOffer offer = new(entry, started.ConversationId, started.From, started.To, started.Headers ?? NoHeaders, transport);
            PhoneCallAdmission admission = await PhoneCall.AdmitAsync(calls, offer, connectionToken, OnCallReplaced).ConfigureAwait(false);
            if (admission.Call is not { } call)
            {
                if (previous is not null)
                {
                    await CloseReplacedAsync(previous).ConfigureAwait(false);
                }

                Refuse(admission.Refusal, admission.HeldByCall);
                return false;
            }

            // Every admitted call reaches StartAsync or AbandonAsync, whatever the start does: one that reaches
            // neither keeps its conversation busy.
            bool callStarted = false;
            try
            {
                await call.StartAsync(connectionToken).ConfigureAwait(false);
                callStarted = true;
            }
            catch (CallConversationLostException)
            {
                CallLog.ConversationHeldByCall(calls.Logger, call.CallId, call.ConversationId);
                Refuse(CallRefusal.Busy, heldByCall: true);
                return false;
            }
            catch (InvalidOperationException) when (call.HasEnded)
            {
                Refuse(CallRefusal.Unavailable, heldByCall: false);
                return false;
            }
            finally
            {
                if (!callStarted)
                {
                    await call.AbandonAsync().ConfigureAwait(false);
                    if (previous is not null)
                    {
                        await CloseReplacedAsync(previous).ConfigureAwait(false);
                    }
                }
            }

            _call = call;
            ConversationSession session = call.Session;
            _session = session;

            // Taken over, the previous call closes nothing; one the gate moved to another conversation closes its own.
            if (previous is not null)
            {
                await observer.ObserveAsync(previous.CloseAsync().AsTask(), ConnectionTaskKind.SessionClose, closeTimeout).ConfigureAwait(false);
            }

            if (_activity is { } activity)
            {
                activity.Rebind(session);
                return true;
            }

            VoiceActivity begun = new(
                _voice,
                session,
                connectionToken,
                fillers: _options.Fillers,
                heardTextWait: _options.HeardTextWait,
                agentLine: HoldAgentLine);
            _userTurns = new UserTurnHandler(_voice, begun);
            _activity = begun;
            _voice.Start();
            return true;
        }

        // Closed already: a refused admission must leave EndAsync nothing of this loop's own to end.
        private async Task CloseReplacedAsync(PhoneCall previous)
        {
            await observer.ObserveAsync(previous.CloseAsync().AsTask(), ConnectionTaskKind.SessionClose, closeTimeout).ConfigureAwait(false);
            _call = null;
            _session = null;
        }

        // A call this loop's own setup frame took over leaves the connection up.
        private void OnCallReplaced(PhoneCall call)
        {
            if (!ReferenceEquals(call, _handingOver))
            {
                replaced?.Invoke();
            }
        }

        private void Refuse(CallRefusal? refusal, bool heldByCall)
        {
            WasRefused = true;
            Refusal = refusal;
            HeldByCall = heldByCall;
        }

        private async Task OnFinalUtteranceAsync(string text)
        {
            if (_userTurns is not { } userTurns)
            {
                // Log once for the conversation, not once for the utterance.
                if (!_loggedPromptBeforeSetup)
                {
                    _loggedPromptBeforeSetup = true;
                    VoiceConversationLog.PromptBeforeSetup(logger);
                }

                return;
            }

            await KeepSessionAliveAsync().ConfigureAwait(false);

            HearCaller(text);
            userTurns.OnFinalTranscript(text);
        }

        // Every line, caller or agent, is a link of one chain, raised only once the link before it was: LineSpoken
        // order is speech order. An agent link holds the chain until the caller can hear no more of its reply,
        // so the caller's next words wait behind it.
        private void HearCaller(string text)
        {
            if (_call is not { } call)
            {
                return;
            }

            lock (_linesGate)
            {
                ChainLocked(() => call.HeardAsync(Speaker.Caller, text).AsTask());
            }
        }

        private void HoldAgentLine(ReplyHearing hearing)
        {
            if (_call is not { } call)
            {
                return;
            }

            lock (_linesGate)
            {
                ChainLocked(() => RaiseAgentLineAsync(call, hearing));
            }
        }

        private static async Task RaiseAgentLineAsync(PhoneCall call, ReplyHearing hearing)
        {
            string heard = await hearing.HeardWhenSettledAsync().ConfigureAwait(false);
            if (heard.Length > 0)
            {
                // The reply's own turn: a barge-in may have started a newer turn since.
                await call.HeardAsync(Speaker.Agent, heard, turnIndex: hearing.TurnIndex).ConfigureAwait(false);
            }
        }

        // Queued, never inline, so no store lookup runs under the lock; a link follows the one before it however that
        // one ended. The window close that releases an agent link never waits on the chain, so the chain cannot lock up.
        private void ChainLocked(Func<Task> raise)
        {
            Task line = _lines = _lines
                .ContinueWith(_ => raise(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default)
                .Unwrap();

            // ReadLoop: no task kind names a line, and PhoneCall already catches and logs a line's own store fault.
            _ = observer.ObserveAsync(line, ConnectionTaskKind.ReadLoop);
        }

        /// <summary>Tells the store this conversation is still being had.</summary>
        /// <returns>A task that completes once the store has been read.</returns>
        private async Task KeepSessionAliveAsync()
        {
            if (_session is not { } session)
            {
                return;
            }

            try
            {
                _ = await calls.Sessions.TryGetAsync(entry, session.ConversationId, connectionToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (connectionToken.IsCancellationRequested)
            {
                // The caller hung up after speaking: the touch is moot, but the words are still a line.
            }
            catch (Exception fault) when (fault is not OperationCanceledException)
            {
                ConnectionTaskObserver.SafeLog(() => VoiceConversationLog.SessionTouchFaulted(
                    logger, session.ConversationId, fault));
            }
        }

        private void Interrupt(ConversationInput.Barge barge)
        {
            if (_activity is not { } activity || _session is not { } session)
            {
                return;
            }

            // Stopped first: the interrupted speech still flushes its step as it tears down, and that closing
            // token must meet a reply already stopped rather than reach the transport after the interrupt.
            _ = observer.ObserveAsync(output.StopAsync(connectionToken).AsTask(), ConnectionTaskKind.WriteLoop);

            // The latency is recorded once every speech it interrupted is done; nothing here waits for that.
            _ = activity.InterruptByAudioActivity(barge.HeardText, barge.PlayedDuration);

            VoiceConversationLog.InterruptReceived(logger, session.ConversationId);
        }
    }
}
