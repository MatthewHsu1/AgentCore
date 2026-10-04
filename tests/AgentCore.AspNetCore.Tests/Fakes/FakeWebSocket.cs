using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Vendors.TelnyxRelay;
using AgentCore.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.Tests.Fakes
{
    /// <summary>
    /// A <see cref="WebSocket"/> a test drives by hand, with no Kestrel and no client socket.
    /// </summary>
    internal sealed class FakeWebSocket : WebSocket
    {
        private static readonly byte[] ReceiveFaultMarker = [];

        private readonly Channel<byte[]?> _inbound = Channel.CreateUnbounded<byte[]?>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        private readonly Lock _gate = new();
        private readonly List<string> _sent = [];
        private readonly TaskCompletionSource _parked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _sendStalled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private volatile bool _parkNextStateRead;
        private Exception? _sendFault;
        private Exception? _receiveFault;
        private Exception? _closeFault;
        private WebSocketState? _goneOnSend;
        private volatile bool _stallSends;
        private bool _abortDuringClose;
        private WebSocketState _state = WebSocketState.Open;

        /// <summary>Gets a task that completes once the write loop parks inside <see cref="State"/>.</summary>
        public Task Parked => _parked.Task;

        /// <summary>Gets a task that completes once a send is stalled by <see cref="StallSends"/>.</summary>
        public Task SendStalled => _sendStalled.Task;

        /// <summary>Gets the status and description <c>CloseOutputAsync</c> was called with, or null.</summary>
        public (WebSocketCloseStatus Status, string? Description)? CloseSent { get; private set; }

        /// <inheritdoc />
        public override WebSocketCloseStatus? CloseStatus => null;

        /// <inheritdoc />
        public override string? CloseStatusDescription => null;

        /// <inheritdoc />
        public override string? SubProtocol => null;

        /// <inheritdoc />
        public override WebSocketState State
        {
            get
            {
                // Read before the park, and answered from that read. The loop asked "is the socket
                // usable" at the moment it took the item, and teardown can run while it is parked; an
                // answer taken after the park would report the socket already closing, the loop would
                // return before its own gate, and the gate would then look proven by a test that never
                // reached it.
                WebSocketState state = _state;

                if (_parkNextStateRead)
                {
                    _parkNextStateRead = false;
                    _ = _parked.TrySetResult();

                    // Blocking on purpose, and only ever on the write loop's own thread. An async park
                    // is not available: this is a property, and the loop reads it synchronously.
                    _released.Task.GetAwaiter().GetResult();
                }

                return state;
            }
        }

        /// <summary>Gets every message the connection sent, as text, in order.</summary>
        public IReadOnlyList<string> Sent
        {
            get
            {
                lock (_gate)
                {
                    return [.. _sent];
                }
            }
        }

        /// <summary>Arms the next read of <see cref="State"/> to block until <see cref="ReleaseParkedState"/>.</summary>
        public void ParkNextStateRead()
        {
            _parkNextStateRead = true;
        }

        /// <summary>Lets the parked read of <see cref="State"/> finish, and never parks again.</summary>
        public void ReleaseParkedState()
        {
            _parkNextStateRead = false;
            _ = _released.TrySetResult();
        }

        /// <summary>Makes every send throw, so the write loop faults.</summary>
        /// <param name="fault">The cause the loop takes.</param>
        public void FailEverySend(Exception fault)
        {
            _sendFault = fault;
        }

        /// <summary>Makes the next send find the socket gone, as a managed socket does once the peer dropped or the close ran.</summary>
        /// <param name="state">What the socket is by then: <see cref="WebSocketState.Aborted"/> or <see cref="WebSocketState.Closed"/>.</param>
        public void GoneOnSend(WebSocketState state)
        {
            _goneOnSend = state;
        }

        /// <summary>Makes every send hang until its token is cancelled, as a send stuck on a peer that stopped reading.</summary>
        public void StallSends()
        {
            _stallSends = true;
        }

        /// <summary>Makes the close find the socket aborted under it by a cancelled send, which surfaces as a cancellation.</summary>
        public void AbortDuringClose()
        {
            _abortDuringClose = true;
        }

        /// <summary>Queues one inbound message, exactly as the vendor would send it.</summary>
        /// <param name="json">The frame.</param>
        public void Queue(string json)
        {
            _ = _inbound.Writer.TryWrite(Encoding.UTF8.GetBytes(json));
        }

        /// <summary>Queues a receive that throws, after every message already queued, so the read loop faults.</summary>
        /// <param name="fault">The cause the loop takes.</param>
        public void FailReceive(Exception fault)
        {
            _receiveFault = fault;
            _ = _inbound.Writer.TryWrite(ReceiveFaultMarker);
        }

        /// <summary>Makes the close throw.</summary>
        /// <param name="fault">What <c>CloseOutputAsync</c> throws.</param>
        public void FailClose(Exception fault)
        {
            _closeFault = fault;
        }

        /// <summary>Queues the vendor's own close frame, which ends the read loop.</summary>
        public void QueueClose()
        {
            _ = _inbound.Writer.TryWrite(null);
        }

        /// <inheritdoc />
        public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            byte[]? message = await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

            if (ReferenceEquals(message, ReceiveFaultMarker))
            {
                throw _receiveFault!;
            }

            if (message is null)
            {
                _state = WebSocketState.CloseReceived;
                return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, endOfMessage: true);
            }

            if (message.Length > buffer.Length)
            {
                throw new InvalidOperationException(
                    $"a queued frame of {message.Length} bytes does not fit the connection's own "
                    + $"{buffer.Length}-byte buffer. This fake sends one message as one fragment.");
            }

            message.CopyTo(buffer.Span);
            return new ValueWebSocketReceiveResult(message.Length, WebSocketMessageType.Text, endOfMessage: true);
        }

        /// <inheritdoc />
        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            if (_stallSends)
            {
                return StallAsync(cancellationToken);
            }

            Record(buffer.AsMemory().Span);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public override ValueTask SendAsync(
            ReadOnlyMemory<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            if (_stallSends)
            {
                return new ValueTask(StallAsync(cancellationToken));
            }

            Record(buffer.Span);
            return ValueTask.CompletedTask;
        }

        /// <summary>Records one send, or throws the fault a test injected.</summary>
        /// <param name="buffer">The bytes the connection handed over.</param>
        private void Record(ReadOnlySpan<byte> buffer)
        {
            if (_sendFault is { } fault)
            {
                throw fault;
            }

            if (_goneOnSend is { } gone)
            {
                _state = gone;
                throw new WebSocketException(
                    WebSocketError.InvalidState,
                    $"The WebSocket is in an invalid state ('{gone}') for this operation. Valid states are: 'Open, CloseReceived'");
            }

            lock (_gate)
            {
                _sent.Add(Encoding.UTF8.GetString(buffer));
            }
        }

        private async Task StallAsync(CancellationToken cancellationToken)
        {
            _ = _sendStalled.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _state = WebSocketState.Aborted;
            }
        }

        /// <inheritdoc />
        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            if (_closeFault is { } fault)
            {
                throw fault;
            }

            if (_abortDuringClose)
            {
                _state = WebSocketState.Aborted;
                throw new OperationCanceledException("the send the close waited behind was cancelled, and it aborted the socket.");
            }

            CloseSent = (closeStatus, statusDescription);
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException("the connection closes with CloseOutputAsync, never CloseAsync.");
        }

        /// <inheritdoc />
        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException("the connection receives into a Memory<byte>.");
        }

        /// <inheritdoc />
        public override void Abort()
        {
            _state = WebSocketState.Aborted;
        }

        /// <inheritdoc />
        public override void Dispose()
        {
            _state = WebSocketState.Closed;
            _ = _inbound.Writer.TryComplete();
            ReleaseParkedState();
        }
    }
}
