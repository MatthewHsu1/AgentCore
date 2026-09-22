using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using AgentCore.Application.Conversation;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.AspNetCore.Conversation;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.Domain.Audit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.Vendors.TelnyxRelay
{
    /// <summary>
    /// One socket, one conversation.
    /// </summary>
    internal sealed class TelnyxRelayConnection : IConversationInputPort, IConversationOutputPort
    {
        private readonly TelnyxRelayOptions _options;

        private readonly ILogger _logger;

        private readonly Channel<OutboundItem> _outbound;

        private readonly CancellationTokenSource _cancellation;

        private readonly CancellationToken _connectionToken;

        private readonly IHostApplicationLifetime _lifetime;

        private readonly ConnectionTaskObserver _observer;

        private readonly JsonWebSocketPump _pump;

        private long _replyGeneration;

        private int _listening;

        private volatile ConversationSession? _session;

        private volatile IConversationSessions? _entrySessions;

        private const string BeforeSetupConversationId = "(before setup)";

        private volatile ConversationTurnArbiter? _arbiter;

        private readonly TimeProvider _timeProvider;

        private bool _loggedPromptBeforeSetup;

        private bool _loggedMalformedInterrupt;

        private bool _loggedSecondSetup;

        /// <summary>Reads this socket's entry session store, resolved once from the URL's entry.</summary>
        /// <returns>The store for the entry the URL named.</returns>
        private IConversationSessions EntrySessions()
        {
            return _entrySessions
                        ?? throw new InvalidOperationException("This socket resolved no session store.");
        }

        private TelnyxRelayConnection(HttpContext http, WebSocket socket, TelnyxRelayOptions options, ILogger logger)
        {
            _options = options;

            _logger = logger;

            _lifetime = http.RequestServices.GetRequiredService<IHostApplicationLifetime>();

            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                http.RequestAborted,
                _lifetime.ApplicationStopping);

            _connectionToken = _cancellation.Token;

            TimeProvider timeProvider = http.RequestServices.GetRequiredService<TimeProvider>();

            _timeProvider = timeProvider;

            // Bounded, so a slow relay slows the turn loop instead of growing a queue without a bound.
            _outbound = Channel.CreateBounded<OutboundItem>(new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });

            _observer = new ConnectionTaskObserver(
                () => _session?.ConversationId ?? BeforeSetupConversationId,
                (conversationId, taskName) => TelnyxRelayLog.TeardownTimedOut(_logger, conversationId, taskName),
                LogFault,
                ClassifyTelnyxFault);

            _pump = new JsonWebSocketPump(
                socket,
                new JsonWebSocketPumpOptions(
                    _options.MaxFrameBytes,
                    _options.IdleTimeout,
                    _options.CloseTimeout,
                    (status, message) => new RelayProtocolException(status, message),
                    frameType => TelnyxRelayLog.UnknownFrameType(_logger, frameType, _session?.ConversationId ?? BeforeSetupConversationId),
                    frameType => TelnyxRelayLog.FrameBodyRefused(_logger, frameType, _session?.ConversationId ?? BeforeSetupConversationId),
                    () => TelnyxRelayLog.IdleTimeoutReached(_logger, _session?.ConversationId ?? BeforeSetupConversationId)),
                timeProvider,
                _connectionToken);
        }

        /// <summary>Runs one conversation to its end.</summary>
        /// <param name="http">The request that carried the handshake.</param>
        /// <param name="socket">The accepted socket.</param>
        /// <param name="options">What the endpoint may do.</param>
        /// <returns>A task that completes when the socket is closed.</returns>
        public static Task RunAsync(HttpContext http, WebSocket socket, TelnyxRelayOptions options)
        {
            ArgumentNullException.ThrowIfNull(http);
            ArgumentNullException.ThrowIfNull(socket);
            ArgumentNullException.ThrowIfNull(options);

            ILogger logger = http.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("AgentCore.TelnyxRelay");

            TelnyxRelayConnection connection = new(http, socket, options, logger)
            {
                _entrySessions = http.RequestServices
                    .GetRequiredService<AgentCoreBoot>()
                    .Entries.ForSessions(ConversationEndpointRouteBuilderExtensions.EntryOf(http))
            };

            return connection.RunAsync();
        }

        private async Task RunAsync()
        {
            Task reading = _pump.ReadLoopAsync(ParseFrame, frame => DispatchAsync((RelayFrame)frame));
            Task writing = _pump.WriteLoopAsync(_outbound.Reader, EncodeOutbound);

            try
            {
                _ = await Task.WhenAny(reading, writing).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    try
                    {
                        await _cancellation.CancelAsync().ConfigureAwait(false);
                    }
                    catch (Exception fault)
                    {
                        ConnectionTaskObserver.SafeLog(() => TelnyxRelayLog.CancellationFaulted(
                            _logger,
                            _session?.ConversationId ?? BeforeSetupConversationId,
                            fault));
                    }

                    await _observer.ObserveAsync(reading, ConnectionTaskKind.ReadLoop).ConfigureAwait(false);


                    Task lastTurn = _arbiter?.CurrentTurn ?? Task.CompletedTask;

                    await _observer.ObserveAsync(lastTurn, ConnectionTaskKind.Turn, _options.CloseTimeout).ConfigureAwait(false);

                    _ = _outbound.Writer.TryComplete();

                    (WebSocketCloseStatus status, string? description) = DetermineCloseStatus(reading, lastTurn, writing);

                    try
                    {
                        await _pump.CloseAsync(status, description).ConfigureAwait(false);
                    }
                    catch (Exception fault)
                    {
                        ConnectionTaskObserver.SafeLog(() => TelnyxRelayLog.CloseFaulted(
                            _logger,
                            _session?.ConversationId ?? BeforeSetupConversationId,
                            fault));
                    }

                    await _observer.ObserveAsync(writing, ConnectionTaskKind.WriteLoop).ConfigureAwait(false);

                    if (_session is { } session)
                    {
                        try
                        {
                            _ = session.EndConversation(EndReasonOf(status));
                        }
                        catch (Exception fault)
                        {
                            ConnectionTaskObserver.SafeLog(() => TelnyxRelayLog.ConversationEndFaulted(
                                _logger,
                                session.ConversationId,
                                fault));
                        }

                        await _observer
                            .ObserveAsync(
                                CloseSessionAsync(session.ConversationId),
                                ConnectionTaskKind.SessionClose,
                                _options.CloseTimeout)
                            .ConfigureAwait(false);
                    }
                }
                finally
                {
                    _cancellation.Dispose();
                }
            }
        }

        /// <summary>Ends one conversation, as a task the observer can bound and watch.</summary>
        /// <param name="conversationId">The conversation that ended.</param>
        /// <returns>A task that completes once the words are written and the session is gone.</returns>
        private async Task CloseSessionAsync(string conversationId)
        {
            await EntrySessions().CloseAsync(conversationId, CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>Works out the status and the description the vendor sees on the close frame.</summary>
        /// <param name="reading">
        /// The read loop's task, already fully observed by <see cref="ConnectionTaskObserver.ObserveAsync"/>.
        /// </param>
        /// <param name="lastTurn">
        /// The last turn's task, already fully observed by <see cref="ConnectionTaskObserver.ObserveAsync"/>.
        /// </param>
        /// <param name="writing">
        /// The write loop's task. Unlike <paramref name="reading"/> and <paramref name="lastTurn"/>, this
        /// one is not yet observed — <c>RunAsync</c> only awaits it after the close this method's result
        /// feeds. Its <see cref="Task.IsFaulted"/> is still safe to read without observing it first:
        /// that flag reflects the task's current state the moment it is read, and reading it does not
        /// itself count as observing the exception for the unobserved-task-exception machinery the way
        /// awaiting or reading <see cref="Task.Exception"/> would.
        /// </param>
        /// <returns>The status to close with, and the description to send alongside it.</returns>
        private (WebSocketCloseStatus Status, string? Description) DetermineCloseStatus(
            Task reading,
            Task lastTurn,
            Task writing)
        {
            if (reading.IsFaulted && reading.Exception.GetBaseException() is RelayProtocolException protocol)
            {
                return (protocol.Status, protocol.Message);
            }

            if (lastTurn.IsFaulted || writing.IsFaulted)
            {
                return (WebSocketCloseStatus.InternalServerError, null);
            }

            if (_lifetime.ApplicationStopping.IsCancellationRequested)
            {
                return (WebSocketCloseStatus.EndpointUnavailable, null);
            }

            if (reading.IsFaulted
                && reading.Exception.GetBaseException() is not WebSocketException
                {
                    WebSocketErrorCode: WebSocketError.ConnectionClosedPrematurely,
                })
            {
                return (WebSocketCloseStatus.InternalServerError, null);
            }

            return (WebSocketCloseStatus.NormalClosure, null);
        }

        /// <summary>Reads the ending one close status reports, as one member of the closed set.</summary>
        /// <param name="status">What <see cref="DetermineCloseStatus"/> already decided this conversation closes with.</param>
        /// <returns>The reason the last event of the chain carries.</returns>
        private static ConversationEndReason EndReasonOf(WebSocketCloseStatus status)
        {
            return status is WebSocketCloseStatus.NormalClosure
                        ? ConversationEndReason.CallerHungUp
                        : ConversationEndReason.Faulted;
        }

        /// <summary>Logs the two faults that are the vendor's doing rather than this endpoint's defect.</summary>
        /// <param name="fault">The exception a loop or a turn ended with.</param>
        /// <param name="kind">Which task faulted.</param>
        /// <param name="conversationId">The id of the conversation, or a placeholder before setup.</param>
        /// <returns>
        /// <see langword="true"/> when this method logged <paramref name="fault"/> itself, and
        /// <see langword="false"/> to leave it to <see cref="LogFault"/>.
        /// </returns>
        private bool ClassifyTelnyxFault(Exception fault, ConnectionTaskKind kind, string conversationId)
        {
            switch (fault)
            {
                case RelayProtocolException protocol:
                    TelnyxRelayLog.RelayProtocolViolation(_logger, conversationId, protocol.Message);
                    return true;

                case WebSocketException { WebSocketErrorCode: WebSocketError.ConnectionClosedPrematurely }
                    when kind == ConnectionTaskKind.ReadLoop:
                    TelnyxRelayLog.ConversationDroppedWithNoCloseFrame(_logger, conversationId);
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>Logs the fault of a read loop, a turn, or a write loop, whichever <paramref name="kind"/> names.</summary>
        /// <param name="kind">Which task faulted.</param>
        /// <param name="conversationId">The id of the conversation, or a placeholder before setup.</param>
        /// <param name="fault">The cause.</param>
        /// <remarks>
        /// The <c>logFault</c> hook of <see cref="ConnectionTaskObserver"/>, which calls it from
        /// <see cref="ConnectionTaskObserver.ObserveAsync"/> directly and from the fault-only
        /// continuation that method attaches on a timeout. That continuation runs with nothing above it
        /// to catch a throw, so the observer wraps every call to this method in
        /// <see cref="ConnectionTaskObserver.SafeLog"/>; nothing here has to guard itself again.
        /// </remarks>
        private void LogFault(ConnectionTaskKind kind, string conversationId, Exception fault)
        {
            switch (kind)
            {
                case ConnectionTaskKind.Turn:
                    TelnyxRelayLog.TurnFaulted(_logger, conversationId, fault);
                    break;

                case ConnectionTaskKind.WriteLoop:
                    TelnyxRelayLog.WriteLoopFaulted(_logger, conversationId, fault);
                    break;

                case ConnectionTaskKind.SessionClose:
                    TelnyxRelayLog.ConversationCloseFaulted(_logger, conversationId, fault);
                    break;

                case ConnectionTaskKind.ReadLoop:
                    TelnyxRelayLog.ReadLoopFaulted(_logger, conversationId, fault);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, "The connection task vocabulary is closed, and this value is not in it.");
            }
        }

        /// <summary>Reads one inbound message with the vendor's own reader.</summary>
        /// <param name="utf8">The whole message, already reassembled by the pump.</param>
        /// <returns>The frame, the name of a type this build does not model, or the name of one whose body would not bind.</returns>
        private static FrameOutcome ParseFrame(ReadOnlySpan<byte> utf8)
        {
            _ = TelnyxRelayFrameReader.TryRead(utf8, out RelayFrame? frame, out string? unknownType, out string? refusedType);
            return new FrameOutcome(frame, unknownType, refusedType);
        }

        /// <summary>Writes one queued item as the frame to send, or drops it.</summary>
        /// <param name="item">What a reply queued, and the generation it was queued under.</param>
        /// <param name="writer">The write loop's own writer, already reset for this frame.</param>
        /// <returns>
        /// <see langword="true"/> when a whole frame was written, and <see langword="false"/> to drop
        /// <paramref name="item"/> without sending anything.
        /// </returns>
        private bool EncodeOutbound(OutboundItem item, Utf8JsonWriter writer)
        {
            if (item.Generation is { } generation && Interlocked.Read(ref _replyGeneration) >= generation)
            {
                return false;
            }

            JsonSerializer.Serialize(writer, item.Frame, item.Frame.GetType(), TelnyxRelayJson.Options);

            return true;
        }

        private async Task DispatchAsync(RelayFrame frame)
        {
            switch (frame)
            {
                case RelayFrame.Setup setup:
                    await StartConversationAsync(setup).ConfigureAwait(false);
                    break;

                case RelayFrame.Prompt { Last: true } prompt:
                    await StartTurnAsync(prompt.VoicePrompt).ConfigureAwait(false);
                    break;

                case RelayFrame.Prompt:
                    // An interim transcript. The turn starts on the final one.
                    break;

                case RelayFrame.Interrupt interrupt:
                    HandleInterrupt(interrupt);
                    break;

                case RelayFrame.Dtmf:
                    // Never the digit itself. A keypad carries card numbers, PINs, and dates of birth,
                    // and the house rule in Log.cs is that no line carries what the caller said.
                    TelnyxRelayLog.DtmfReceived(_logger);
                    break;

                case RelayFrame.Error error:
                    // The vendor refused a frame this endpoint sent. That is our defect.
                    TelnyxRelayLog.FrameRefused(_logger, _session?.ConversationId ?? BeforeSetupConversationId, error.Description);
                    break;

                default:
                    // The reader hands back only the frames this build models; see TelnyxRelayFrameReader.TryRead.
                    break;
            }
        }

        private async Task StartConversationAsync(RelayFrame.Setup setup)
        {
            IConversationSessions sessions = EntrySessions();

            if (_session is { } replaced)
            {
                if (!_loggedSecondSetup)
                {
                    _loggedSecondSetup = true;
                    TelnyxRelayLog.SecondSetupFrame(_logger, replaced.ConversationId);
                }

                await _observer
                    .ObserveAsync(
                        CloseSessionAsync(replaced.ConversationId),
                        ConnectionTaskKind.SessionClose,
                        _options.CloseTimeout)
                    .ConfigureAwait(false);
            }

            _session = await sessions.OpenAsync(setup.ConversationSessionId, _cancellation.Token).ConfigureAwait(false);

            if (_arbiter is { } arbiter)
            {
                arbiter.Rebind(_session);
                return;
            }

            _arbiter = new ConversationTurnArbiter(
                _session,
                this,
                _observer,
                conversationId => TelnyxRelayLog.PromptHeld(_logger, conversationId),
                conversationId => TelnyxRelayLog.PendingPromptDropped(_logger, conversationId),
                _connectionToken);
        }

        private async Task StartTurnAsync(string text)
        {
            ConversationTurnClock clock = new(_timeProvider);

            if (_arbiter is not { } arbiter)
            {
                // Log once for the conversation, not once for the frame. Same rule as the unknown-frame line.
                if (!_loggedPromptBeforeSetup)
                {
                    _loggedPromptBeforeSetup = true;
                    TelnyxRelayLog.PromptBeforeSetup(_logger);
                }

                return;
            }

            await KeepSessionAliveAsync().ConfigureAwait(false);

            _ = arbiter.StartTurnAsync(text, clock);
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
                _ = await EntrySessions().TryGetAsync(session.ConversationId, _connectionToken).ConfigureAwait(false);
            }
            catch (Exception fault) when (fault is not OperationCanceledException)
            {
                ConnectionTaskObserver.SafeLog(() => TelnyxRelayLog.SessionTouchFaulted(
                    _logger, session.ConversationId, fault));
            }
        }

        private void HandleInterrupt(RelayFrame.Interrupt interrupt)
        {
            long detected = _timeProvider.GetTimestamp();

            if (_arbiter is not { } arbiter || _session is not { } session)
            {
                return;
            }

            if (interrupt.UtteranceUntilInterrupt is null || interrupt.DurationUntilInterruptMs < 0)
            {
                if (!_loggedMalformedInterrupt)
                {
                    _loggedMalformedInterrupt = true;
                    TelnyxRelayLog.MalformedInterruptFrame(_logger, session.ConversationId);
                }

                return;
            }

            _ = arbiter.Interrupt(
                interrupt.UtteranceUntilInterrupt,
                TimeSpan.FromMilliseconds(interrupt.DurationUntilInterruptMs));

            AgentCoreTelemetry.RecordBargeInLatency(_timeProvider.GetElapsedTime(detected));

            TelnyxRelayLog.InterruptReceived(_logger, session.ConversationId);
        }

        /// <inheritdoc />
        public ValueTask SpeakAsync(string fragment, CancellationToken cancellationToken = default)
        {
            return _outbound.Writer.WriteAsync(
                        new OutboundItem(CurrentGeneration() + 1, new RelayToken(fragment, Last: false)),
                        cancellationToken);
        }

        /// <inheritdoc />
        public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
        {
            // The vendor closes a reply on last: true, and the sample uses an empty final token when
            // the stream ended with no trailing text.
            return _outbound.Writer.WriteAsync(
                new OutboundItem(CurrentGeneration() + 1, new RelayToken(string.Empty, Last: true)),
                cancellationToken);
        }

        /// <inheritdoc />
        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref _replyGeneration);

            while (_outbound.Reader.TryRead(out _))
            {
                // Drained, never sent: the barge-in above already cut off everything still queued.
            }

            return ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public IAsyncEnumerable<ConversationInput> ListenAsync(CancellationToken cancellationToken = default)
        {
            return Interlocked.CompareExchange(ref _listening, 1, 0) != 0
                ? throw new InvalidOperationException("This relay connection is already being read.")
                : ListenCoreAsync(cancellationToken);
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        /// <summary>Yields nothing, and completes when this connection ends.</summary>
        /// <param name="cancellationToken">Ends the stream early, ahead of the connection itself.</param>
        /// <returns>An empty stream, deliberately. <see cref="ListenAsync"/>'s remarks say why.</returns>
        private async IAsyncEnumerable<ConversationInput> ListenCoreAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // Linked, so the caller's own token ends the stream too, and built from the captured
            // connection token rather than from _cancellation.Token for the reason that field records.
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_connectionToken, cancellationToken);

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            yield break;
        }

        /// <summary>Reads the newest reply generation a barge-in has cut off, or 0 when none has.</summary>
        /// <returns>The counter, read the same way the write loop's own gate reads it.</returns>
        private long CurrentGeneration()
        {
            return Interlocked.Read(ref _replyGeneration);
        }
    }

    /// <summary>The relay broke the contract, and the socket must close with a reason.</summary>
    /// <param name="status">The close status the vendor should see.</param>
    /// <param name="message">Why the endpoint refused.</param>
    internal sealed class RelayProtocolException(WebSocketCloseStatus status, string message)
        : Exception(message)
    {
        /// <summary>Gets the status the socket closes with.</summary>
        public WebSocketCloseStatus Status { get; } = status;
    }

    /// <summary>One item queued for the write loop, carrying the reply generation it was written under.</summary>
    /// <param name="Generation">
    /// The reply generation <see cref="Frame"/> was queued under, or <see langword="null"/> for a
    /// frame no reply owns — nothing writes one of those yet, but the write loop's gate only applies
    /// when a generation is present, so a future frame outside the reply lifecycle (a close handoff,
    /// for one) can opt out by carrying none. This type never reaches <see cref="TelnyxRelayJson"/>:
    /// only <see cref="Frame"/> is serialized, so nothing here changes the wire.
    /// </param>
    /// <param name="Frame">The frame to serialize and send: a <see cref="RelayToken"/> today.</param>
    internal readonly record struct OutboundItem(long? Generation, object Frame);
}
