using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Vendors.TelnyxRelay;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.Tests.Fakes;

/// <summary>
/// A <see cref="WebSocket"/> a test drives by hand, with no Kestrel and no client socket.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TelnyxRelayHost"/> is still the right fake for anything the wire owns: framing, a
/// fragmented message, real backpressure, and the close handshake. Three things it cannot reach,
/// and this class exists for exactly those three.
/// </para>
/// <list type="number">
/// <item><description>
/// <b>The write loop's turn-id gate.</b> The gate sits between the dequeue and the one
/// <c>SendAsync</c> call the connection makes, and no real socket lets a test stop the loop in that
/// gap. <see cref="ParkNextStateRead"/> does: the write loop reads <see cref="State"/> immediately
/// after it dequeues an item, so parking that one read parks the loop with the item in hand and
/// the gate still ahead of it.
/// </description></item>
/// <item><description>
/// <b>The close status.</b> <see cref="CloseSent"/> records the arguments rather than the bytes, so
/// a status still reaches a test after a real socket would already have aborted.
/// </description></item>
/// <item><description>
/// <b>A faulting send.</b> <see cref="FailEverySend"/> makes the write loop throw on demand, which
/// nothing on a healthy loopback socket does.
/// </description></item>
/// </list>
/// <para>
/// One queued message is one WebSocket message with one fragment. Fragmentation is a wire behaviour
/// and it already has its own test over a real socket.
/// </para>
/// </remarks>
internal sealed class FakeWebSocket : WebSocket
{
    private readonly Channel<byte[]?> _inbound = Channel.CreateUnbounded<byte[]?>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly Lock _gate = new();
    private readonly List<string> _sent = [];
    private readonly TaskCompletionSource _parked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private volatile bool _parkNextStateRead;
    private Exception? _sendFault;
    private WebSocketState _state = WebSocketState.Open;

    /// <summary>Gets a task that completes once the write loop parks inside <see cref="State"/>.</summary>
    public Task Parked => _parked.Task;

    /// <summary>Gets the status and description <c>CloseOutputAsync</c> was called with, or null.</summary>
    public (WebSocketCloseStatus Status, string? Description)? CloseSent { get; private set; }

    /// <inheritdoc />
    public override WebSocketCloseStatus? CloseStatus => null;

    /// <inheritdoc />
    public override string? CloseStatusDescription => null;

    /// <inheritdoc />
    public override string? SubProtocol => null;

    /// <inheritdoc />
    /// <remarks>
    /// The write loop reads this once for each item it dequeues, before its turn-id gate and before
    /// its one send. That makes this property the only observable point between the two, so parking
    /// here is what lets a test hold an item in the loop's own hand while a barge-in lands.
    /// </remarks>
    public override WebSocketState State
    {
        get
        {
            // Read before the park, and answered from that read. The loop asked "is the socket
            // usable" at the moment it took the item, and teardown can run while it is parked; an
            // answer taken after the park would report the socket already closing, the loop would
            // return before its own gate, and the gate would then look proven by a test that never
            // reached it.
            var state = _state;

            if (_parkNextStateRead)
            {
                _parkNextStateRead = false;
                _parked.TrySetResult();

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
    public void ParkNextStateRead() => _parkNextStateRead = true;

    /// <summary>Lets the parked read of <see cref="State"/> finish, and never parks again.</summary>
    /// <remarks>A test calls this from a <c>finally</c>, so a failed assertion never strands the loop.</remarks>
    public void ReleaseParkedState()
    {
        _parkNextStateRead = false;
        _released.TrySetResult();
    }

    /// <summary>Makes every send throw, so the write loop faults.</summary>
    /// <param name="fault">The cause the loop takes.</param>
    public void FailEverySend(Exception fault) => _sendFault = fault;

    /// <summary>Queues one inbound message, exactly as the vendor would send it.</summary>
    /// <param name="json">The frame.</param>
    public void Queue(string json) => _inbound.Writer.TryWrite(Encoding.UTF8.GetBytes(json));

    /// <summary>Queues the vendor's own close frame, which ends the read loop.</summary>
    public void QueueClose() => _inbound.Writer.TryWrite(null);

    /// <inheritdoc />
    public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var message = await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

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
    /// <remarks>
    /// This is the overload the connection actually reaches. It passes a <c>byte[]</c>, and C#
    /// resolves that to the <see cref="ArraySegment{T}"/> parameter rather than to the
    /// <see cref="ReadOnlyMemory{T}"/> one. A fake that only overrode the memory form would never
    /// see a send at all, and every assertion about what did or did not reach the socket would then
    /// pass for the wrong reason.
    /// </remarks>
    public override Task SendAsync(
        ArraySegment<byte> buffer,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken)
    {
        Record(buffer.AsMemory().Span);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>Kept beside the <see cref="ArraySegment{T}"/> form, so either one a caller picks is recorded.</remarks>
    public override ValueTask SendAsync(
        ReadOnlyMemory<byte> buffer,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken)
    {
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

        lock (_gate)
        {
            _sent.Add(Encoding.UTF8.GetString(buffer));
        }
    }

    /// <inheritdoc />
    public override Task CloseOutputAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken)
    {
        CloseSent = (closeStatus, statusDescription);
        _state = WebSocketState.CloseSent;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The connection never calls this: <c>CloseAsync</c> waits for the peer's close frame, and a
    /// call that already dropped never sends one. A call that reaches here is a defect worth a red
    /// test rather than a silent pass.
    /// </remarks>
    public override Task CloseAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken)
        => throw new NotSupportedException("the connection closes with CloseOutputAsync, never CloseAsync.");

    /// <inheritdoc />
    /// <remarks>The connection receives into a <see cref="Memory{T}"/>, so nothing reaches this form.</remarks>
    public override Task<WebSocketReceiveResult> ReceiveAsync(
        ArraySegment<byte> buffer,
        CancellationToken cancellationToken)
        => throw new NotSupportedException("the connection receives into a Memory<byte>.");

    /// <inheritdoc />
    public override void Abort() => _state = WebSocketState.Aborted;

    /// <inheritdoc />
    /// <remarks>
    /// Completing the inbound channel here is what releases a receive the read loop abandoned to
    /// its idle race. That receive owns a rented buffer until it finishes, so leaving it pending
    /// forever would keep the array out of <c>ArrayPool</c> for the life of the test run.
    /// </remarks>
    public override void Dispose()
    {
        _state = WebSocketState.Closed;
        _inbound.Writer.TryComplete();
        ReleaseParkedState();
    }
}

/// <summary>
/// An application lifetime a test owns, so a test can stop the host without one.
/// </summary>
/// <remarks>
/// The connection links its own token to <see cref="ApplicationStopping"/> and reads that same token
/// again when it picks a close status, so this is the only seam a test needs to prove the
/// host-stopping path.
/// </remarks>
internal sealed class TestHostLifetime : IHostApplicationLifetime, IDisposable
{
    private readonly CancellationTokenSource _started = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly CancellationTokenSource _stopped = new();

    public CancellationToken ApplicationStarted => _started.Token;

    public CancellationToken ApplicationStopping => _stopping.Token;

    public CancellationToken ApplicationStopped => _stopped.Token;

    public void StopApplication()
    {
        _stopping.Cancel();
        _stopped.Cancel();
    }

    public void Dispose()
    {
        _started.Dispose();
        _stopping.Dispose();
        _stopped.Dispose();
    }
}

/// <summary>
/// One <see cref="TelnyxRelayConnection"/> driven straight over a <see cref="FakeWebSocket"/>.
/// </summary>
/// <remarks>
/// <c>RunAsync</c> takes the abstract <see cref="WebSocket"/>, so nothing here needs Kestrel, a
/// port, or a client socket. The service provider is the real one <c>AddAgentCore</c> builds, so
/// the session factory, the store, and the clock behave exactly as they do on a live call.
/// </remarks>
internal sealed class RelayConnectionHarness : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly TestHostLifetime _lifetime;

    private RelayConnectionHarness(
        ServiceProvider services,
        TestHostLifetime lifetime,
        FakeWebSocket socket,
        Task connection)
    {
        _services = services;
        _lifetime = lifetime;
        Socket = socket;
        Connection = connection;
    }

    /// <summary>Gets the socket a test queues frames on and reads sends off.</summary>
    public FakeWebSocket Socket { get; }

    /// <summary>Gets the task that completes when the connection has torn itself down.</summary>
    public Task Connection { get; }

    /// <summary>Starts one connection over one document and one fake model.</summary>
    /// <param name="yaml">The document, as YAML.</param>
    /// <param name="reply">The model behind every agent.</param>
    /// <param name="logging">Anything a test adds to the logging pipeline.</param>
    /// <param name="relay">Anything a test binds on the relay endpoint's own options.</param>
    /// <returns>The running harness.</returns>
    public static async Task<RelayConnectionHarness> StartAsync(
        string yaml,
        IChatClient reply,
        Action<ILoggingBuilder>? logging = null,
        Action<TelnyxRelayOptions>? relay = null)
    {
        ServiceCollection services = new();
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            logging?.Invoke(builder);
        });

        TestHostLifetime lifetime = new();
        services.AddSingleton<IHostApplicationLifetime>(lifetime);
        await services.AddAgentCoreAsync(options =>
        {
            options.Configuration = ConfigurationLoader.LoadYaml(yaml);
            options.UseChatClients(_ => new RoutingChatClientFactory(reply));
        });

        var provider = services.BuildServiceProvider();
        DefaultHttpContext http = new() { RequestServices = provider };

        TelnyxRelayOptions options = new();
        relay?.Invoke(options);

        FakeWebSocket socket = new();
        var connection = TelnyxRelayConnection.RunAsync(http, socket, options);

        return new RelayConnectionHarness(provider, lifetime, socket, connection);
    }

    /// <summary>Stops the host, which is what the <c>EndpointUnavailable</c> close status reports.</summary>
    public void StopApplication() => _lifetime.StopApplication();

    /// <summary>Ends the connection and releases everything behind it.</summary>
    public async ValueTask DisposeAsync()
    {
        // A test that failed early may have left the write loop parked or the read loop waiting, so
        // both are released here before anything waits on the connection task.
        Socket.ReleaseParkedState();
        Socket.QueueClose();

        try
        {
            await Connection.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The connection's own faults belong to whichever assertion a test already made about
            // them. Disposal only has to stop waiting.
        }

        Socket.Dispose();
        _lifetime.Dispose();
        await _services.DisposeAsync().ConfigureAwait(false);
    }
}
