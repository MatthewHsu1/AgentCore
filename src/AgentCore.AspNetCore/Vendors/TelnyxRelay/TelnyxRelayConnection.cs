using System.Net.WebSockets;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Voice;
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
    /// <remarks>
    /// This type owns the socket and its teardown. <see cref="TelnyxRelayInput"/> and
    /// <see cref="TelnyxRelayOutput"/> translate the wire, and <see cref="VoiceConversationLoop"/>
    /// owns the turns.
    /// </remarks>
    internal sealed class TelnyxRelayConnection : IAsyncDisposable
    {
        private readonly TelnyxRelayOptions _options;

        private readonly ILogger _logger;

        private readonly TelnyxRelayInput _input;

        private readonly TelnyxRelayOutput _output = new();

        private readonly VoiceConversationLoop _loop;

        private readonly CancellationTokenSource _cancellation;

        private readonly CancellationToken _connectionToken;

        private readonly IHostApplicationLifetime _lifetime;

        private readonly ConnectionTaskObserver _observer;

        private readonly JsonWebSocketPump _pump;

        private readonly JsonWebSocketSender _sender;

        private const string BeforeSetupConversationId = "(before setup)";

        private const string ConversationInUseDescription = "the conversation is in use by another entry";

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

            _input = new TelnyxRelayInput(logger, () => ConversationIdForLog);

            _observer = new ConnectionTaskObserver(
                () => ConversationIdForLog,
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
                    frameType => TelnyxRelayLog.UnknownFrameType(_logger, frameType, ConversationIdForLog),
                    frameType => TelnyxRelayLog.FrameBodyRefused(_logger, frameType, ConversationIdForLog),
                    () => TelnyxRelayLog.IdleTimeoutReached(_logger, ConversationIdForLog)),
                timeProvider,
                _connectionToken);

            _sender = new JsonWebSocketSender(socket, _options.CloseTimeout, _connectionToken);

            _loop = new VoiceConversationLoop(
                http.RequestServices.GetRequiredService<AgentCoreBoot>().Entries.Sessions,
                ConversationEndpointRouteBuilderExtensions.EntryOf(http),
                _output,
                _observer,
                timeProvider,
                logger,
                _options.CloseTimeout,
                _connectionToken,
                _options.Voice);
        }

        private string ConversationIdForLog => _loop?.ConversationId ?? BeforeSetupConversationId;

        /// <summary>Runs one conversation to its end.</summary>
        /// <param name="http">The request that carried the handshake.</param>
        /// <param name="socket">The accepted socket.</param>
        /// <param name="options">What the endpoint may do.</param>
        /// <returns>A task that completes when the socket is closed.</returns>
        public static async Task RunAsync(HttpContext http, WebSocket socket, TelnyxRelayOptions options)
        {
            ArgumentNullException.ThrowIfNull(http);
            ArgumentNullException.ThrowIfNull(socket);
            ArgumentNullException.ThrowIfNull(options);

            ILogger logger = http.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("AgentCore.TelnyxRelay");

            await using TelnyxRelayConnection connection = new(http, socket, options, logger);

            await connection.RunAsync().ConfigureAwait(false);
        }

        private async Task RunAsync()
        {
            Task reading = _pump.ReadLoopAsync(ParseFrame, AcceptAsync);
            Task writing = _sender.WriteLoopAsync(_output.Reader, _output.Encode, _output.SendGate);
            Task listening = _loop.RunAsync(_input.ListenAsync(CancellationToken.None));

            try
            {
                _ = await Task.WhenAny(reading, writing, listening).ConfigureAwait(false);
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
                            ConversationIdForLog,
                            fault));
                    }

                    // Nothing queued from here on can be sent, and a speech still blocked on a full queue would
                    // otherwise hold the drain below until its interruption backstop.
                    _output.Complete();

                    await _observer.ObserveAsync(reading, ConnectionTaskKind.ReadLoop).ConfigureAwait(false);

                    _input.Complete();

                    await _observer.ObserveAsync(listening, ConnectionTaskKind.ReadLoop).ConfigureAwait(false);

                    Task drained = _loop.DrainAsync();

                    await _observer.ObserveAsync(drained, ConnectionTaskKind.SessionClose, _options.CloseTimeout).ConfigureAwait(false);

                    (WebSocketCloseStatus status, string? description) = DetermineCloseStatus(reading, listening, drained, writing);

                    try
                    {
                        await _sender.CloseAsync(status, description).ConfigureAwait(false);
                    }
                    catch (Exception fault)
                    {
                        ConnectionTaskObserver.SafeLog(() => TelnyxRelayLog.CloseFaulted(
                            _logger,
                            ConversationIdForLog,
                            fault));
                    }

                    await _observer.ObserveAsync(writing, ConnectionTaskKind.WriteLoop).ConfigureAwait(false);

                    await _loop.EndAsync(EndReasonOf(status)).ConfigureAwait(false);
                }
                finally
                {
                    _cancellation.Dispose();
                }
            }
        }

        /// <summary>Works out the status and the description the vendor sees on the close frame.</summary>
        /// <param name="reading">
        /// The read loop's task, already fully observed by <see cref="ConnectionTaskObserver.ObserveAsync"/>.
        /// </param>
        /// <param name="listening">
        /// The voice loop's task, already fully observed. A fault there closes the socket the same way a fault in
        /// the read loop does.
        /// </param>
        /// <param name="drained">
        /// The speech teardown's task, already fully observed by <see cref="ConnectionTaskObserver.ObserveAsync"/>.
        /// </param>
        /// <param name="writing">
        /// The write loop's task. Unlike <paramref name="reading"/> and <paramref name="drained"/>, this
        /// one is not yet observed — <c>RunAsync</c> only awaits it after the close this method's result
        /// feeds. Its <see cref="Task.IsFaulted"/> is still safe to read without observing it first:
        /// that flag reflects the task's current state the moment it is read, and reading it does not
        /// itself count as observing the exception for the unobserved-task-exception machinery the way
        /// awaiting or reading <see cref="Task.Exception"/> would.
        /// </param>
        /// <returns>The status to close with, and the description to send alongside it.</returns>
        private (WebSocketCloseStatus Status, string? Description) DetermineCloseStatus(
            Task reading,
            Task listening,
            Task drained,
            Task writing)
        {
            if (reading.IsFaulted && reading.Exception.GetBaseException() is RelayProtocolException protocol)
            {
                return (protocol.Status, protocol.Message);
            }

            if (drained.IsFaulted || writing.IsFaulted)
            {
                return (WebSocketCloseStatus.InternalServerError, null);
            }

            if (_loop.WasRefused)
            {
                return (WebSocketCloseStatus.PolicyViolation, ConversationInUseDescription);
            }

            if (_lifetime.ApplicationStopping.IsCancellationRequested)
            {
                return (WebSocketCloseStatus.EndpointUnavailable, null);
            }

            if (listening.IsFaulted
                || (reading.IsFaulted
                    && reading.Exception.GetBaseException() is not WebSocketException
                    {
                        WebSocketErrorCode: WebSocketError.ConnectionClosedPrematurely,
                    }))
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
        /// <param name="fault">The exception a loop or the session close ended with.</param>
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

        /// <summary>Logs the fault of a read loop, a write loop, or the session close, whichever <paramref name="kind"/> names.</summary>
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

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await _input.DisposeAsync().ConfigureAwait(false);
            await _output.DisposeAsync().ConfigureAwait(false);
        }

        private Task AcceptAsync(object frame)
        {
            return _input.AcceptAsync((RelayFrame)frame);
        }
    }
}
