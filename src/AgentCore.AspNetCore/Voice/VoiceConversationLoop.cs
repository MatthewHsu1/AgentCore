using AgentCore.Application.Conversation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Domain.Audit;
using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.Voice
{
    /// <summary>
    /// Feeds one voice conversation's inbound events to the turn-taking classes: user turns, replies,
    /// barge-ins, and the away prompt.
    /// </summary>
    /// <param name="sessions">The one session owner, shared by every entry.</param>
    /// <param name="entry">The entry the URL named. Every open and lookup this loop makes names it.</param>
    /// <param name="output">Where every reply goes.</param>
    /// <param name="observer">Watches every task this loop starts.</param>
    /// <param name="time">The clock the host bound.</param>
    /// <param name="logger">The logger of the connection that owns this loop.</param>
    /// <param name="closeTimeout">How long a replaced session may take to close.</param>
    /// <param name="connectionToken">Cancelled once, for any reason the transport is going away.</param>
    /// <param name="options">The away prompt and the per-tool filler, or <see langword="null"/> for <see cref="VoiceOptions.Default"/>.</param>
    internal sealed class VoiceConversationLoop(
        IConversationSessions sessions,
        string entry,
        IConversationOutputPort output,
        ConnectionTaskObserver observer,
        TimeProvider time,
        ILogger logger,
        TimeSpan closeTimeout,
        CancellationToken connectionToken,
        VoiceOptions? options = null)
    {
        private readonly VoiceOptions _options = options ?? VoiceOptions.Default;

        private readonly VoiceSession _voice = new(output, time, logger, (options ?? VoiceOptions.Default).UserAway);

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
        /// Gets whether the loop stopped because the conversation the transport named is held live by another entry.
        /// </summary>
        public bool WasRefused { get; private set; }

        /// <summary>Reads every inbound event, in order, until the stream ends or an open is refused.</summary>
        /// <param name="inputs">The conversation's inbound events.</param>
        /// <returns>A task that completes when the stream ends, or once <see cref="WasRefused"/> is set.</returns>
        public async Task RunAsync(IAsyncEnumerable<ConversationInput> inputs)
        {
            await foreach (ConversationInput input in inputs.ConfigureAwait(false))
            {
                switch (input)
                {
                    case ConversationInput.Started started:
                        if (!await StartConversationAsync(started.ConversationId).ConfigureAwait(false))
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

        /// <summary>Tears the conversation's speech down: LiveKit's session close order, then the engine turns behind it.</summary>
        /// <returns>A task that completes once no speech is left running and every engine turn has ended.</returns>
        public async Task DrainAsync()
        {
            await _voice.CloseAsync().ConfigureAwait(false);

            if (_activity is { } activity)
            {
                await activity.EngineRun.ConfigureAwait(false);
            }
        }

        /// <summary>Closes the audit chain and the session of the conversation, if one started.</summary>
        /// <param name="reason">Why the conversation ended.</param>
        /// <returns>A task that completes once the session is closed or its close timed out.</returns>
        public async Task EndAsync(ConversationEndReason reason)
        {
            if (_session is not { } session)
            {
                return;
            }

            // The id may have moved on: this loop's own session unloaded, and a later caller reopened it.
            // Ending or closing that newer session here would answer this loop's hang-up with someone else's
            // call. A store that cannot be read falls back to today's behaviour, on this loop's own session.
            ConversationSession? current;
            try
            {
                current = await sessions.TryGetAsync(entry, session.ConversationId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception fault) when (fault is not OperationCanceledException)
            {
                ConnectionTaskObserver.SafeLog(() => VoiceConversationLog.SessionTouchFaulted(logger, session.ConversationId, fault));
                current = session;
            }

            if (current is not null && !ReferenceEquals(current, session))
            {
                return;
            }

            try
            {
                _ = session.EndConversation(reason);
            }
            catch (Exception fault)
            {
                ConnectionTaskObserver.SafeLog(() => VoiceConversationLog.ConversationEndFaulted(
                    logger,
                    session.ConversationId,
                    fault));
            }

            await observer
                .ObserveAsync(CloseSessionAsync(session.ConversationId), ConnectionTaskKind.SessionClose, closeTimeout)
                .ConfigureAwait(false);
        }

        private async Task CloseSessionAsync(string conversationId)
        {
            await sessions.CloseAsync(entry, conversationId, CancellationToken.None).ConfigureAwait(false);
        }

        /// <returns><see langword="false"/> when another entry holds the conversation live, so nothing was opened.</returns>
        private async Task<bool> StartConversationAsync(string conversationId)
        {
            if (_session is { } replaced)
            {
                if (!_loggedSecondSetup)
                {
                    _loggedSecondSetup = true;
                    VoiceConversationLog.SecondSetupFrame(logger, replaced.ConversationId);
                }

                // The id may have moved on: replaced already unloaded, and a later caller reopened it. Closing
                // that newer session here would tear down a call this loop never held. A store that cannot be
                // read falls back to today's behaviour, on this loop's own replaced session.
                ConversationSession? current;
                try
                {
                    current = await sessions.TryGetAsync(entry, replaced.ConversationId, connectionToken).ConfigureAwait(false);
                }
                catch (Exception fault) when (fault is not OperationCanceledException)
                {
                    ConnectionTaskObserver.SafeLog(() => VoiceConversationLog.SessionTouchFaulted(logger, replaced.ConversationId, fault));
                    current = replaced;
                }

                if (current is null || ReferenceEquals(current, replaced))
                {
                    await observer
                        .ObserveAsync(
                            CloseSessionAsync(replaced.ConversationId),
                            ConnectionTaskKind.SessionClose,
                            closeTimeout)
                        .ConfigureAwait(false);
                }

                // Closed already: a refused reopen below must leave EndAsync nothing of this loop's own to end.
                _session = null;
            }

            ConversationSession session;
            try
            {
                session = await sessions.GetOrOpenAsync(entry, conversationId, state: null, connectionToken).ConfigureAwait(false);
            }
            catch (ConversationInUseException)
            {
                WasRefused = true;
                VoiceConversationLog.ConversationInUse(logger, conversationId, entry);
                return false;
            }

            _session = session;

            if (_activity is { } activity)
            {
                activity.Rebind(session);
                return true;
            }

            VoiceActivity started = new(
                _voice,
                session,
                connectionToken,
                fillers: _options.Fillers,
                heardTextWait: _options.HeardTextWait);
            _userTurns = new UserTurnHandler(_voice, started);
            _activity = started;
            _voice.Start();
            return true;
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

            userTurns.OnFinalTranscript(text);
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
                _ = await sessions.TryGetAsync(entry, session.ConversationId, connectionToken).ConfigureAwait(false);
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
