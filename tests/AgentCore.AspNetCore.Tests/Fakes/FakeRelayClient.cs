using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Fakes;

/// <summary>
/// The vendor side of the socket, scripted by a test.
/// </summary>
/// <remarks>
/// Telnyx opens the socket to us, so AgentCore is the server and there is nothing to fake on the
/// far side. This class is the client the vendor would be. The socket is real, because framing is
/// a wire behaviour and an in-memory pipe would not prove that the read loop rejoins a fragmented
/// message.
/// </remarks>
internal sealed class FakeRelayClient : IAsyncDisposable
{
    private readonly ClientWebSocket _socket;
    private readonly Channel<RelayItem> _channel = Channel.CreateUnbounded<RelayItem>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _pumpCancellation = new();
    private readonly Task _pump;

    private FakeRelayClient(ClientWebSocket socket)
    {
        _socket = socket;

        // One background loop owns every ReceiveAsync on this socket for its whole life. Cancelling
        // a pending ReceiveAsync moves ClientWebSocket to WebSocketState.Aborted for good, and every
        // later send or close then throws. A reader that gives up early — ReadFramesForAsync on a
        // quiet window — must never be the thing that cancels a receive, so it never touches the
        // socket at all. It only stops waiting on this channel.
        _pump = Task.Run(() => PumpAsync(_pumpCancellation.Token));
    }

    /// <summary>Opens one socket to the host.</summary>
    /// <param name="address">The <c>ws://</c> address of the relay route.</param>
    /// <returns>The connected client.</returns>
    public static async Task<FakeRelayClient> ConnectAsync(Uri address)
    {
        ClientWebSocket socket = new();
        await socket.ConnectAsync(address, TestContext.Current.CancellationToken);
        return new FakeRelayClient(socket);
    }

    /// <summary>Sends one frame as one message.</summary>
    public Task SendAsync(string json) => SendRawAsync(json);

    /// <summary>Sends any text as one message, well-formed or not.</summary>
    public async Task SendRawAsync(string text)
    {
        await _socket.SendAsync(
            Encoding.UTF8.GetBytes(text),
            WebSocketMessageType.Text,
            endOfMessage: true,
            TestContext.Current.CancellationToken);
    }

    /// <summary>Sends one frame split across many WebSocket fragments.</summary>
    /// <param name="json">The frame.</param>
    /// <param name="fragments">How many pieces to split it into.</param>
    /// <remarks>
    /// The vendor may fragment any message, and a read loop that parses one receive at a time
    /// fails on the first piece. This method is how a test proves the loop rejoins them. The
    /// partition is balanced rather than a fixed chunk size: a fixed size rounded up can finish in
    /// fewer sends than asked whenever the byte count divides evenly by a smaller size than the one
    /// computed for <paramref name="fragments"/> pieces, so a caller's requested count would not be
    /// what actually left the wire. Spreading the remainder one byte at a time across the first
    /// pieces instead makes the loop run exactly <paramref name="fragments"/> times by construction.
    /// </remarks>
    public async Task SendFragmentedAsync(string json, int fragments)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        Assert.True(
            fragments > 0 && fragments <= bytes.Length,
            $"cannot split {bytes.Length} bytes into {fragments} non-empty fragments.");

        var baseSize = bytes.Length / fragments;
        var remainder = bytes.Length % fragments;
        var offset = 0;

        for (var i = 0; i < fragments; i++)
        {
            var length = baseSize + (i < remainder ? 1 : 0);
            var last = i == fragments - 1;

            await _socket.SendAsync(
                bytes.AsMemory(offset, length),
                WebSocketMessageType.Text,
                endOfMessage: last,
                TestContext.Current.CancellationToken);

            offset += length;
        }
    }

    /// <summary>Reads one outbound frame.</summary>
    /// <returns>The frame as a node tree.</returns>
    public async Task<JsonNode> ReadFrameAsync()
    {
        var item = await ReadItemAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        Assert.False(item.IsClose, "expected a data frame, but the host closed the socket first.");
        return item.Frame!;
    }

    /// <summary>Reads every text frame of one reply, up to and including the closing one.</summary>
    /// <returns>The token of each frame, in order.</returns>
    public async Task<List<string>> ReadTextFramesUntilLastAsync()
    {
        List<string> tokens = [];
        while (true)
        {
            var frame = await ReadFrameAsync();
            Assert.Equal("text", frame["type"]!.GetValue<string>());
            tokens.Add(frame["token"]!.GetValue<string>());

            if (frame["last"]!.GetValue<bool>())
            {
                return tokens;
            }
        }
    }

    /// <summary>Collects every frame that arrives inside a window.</summary>
    /// <param name="window">How long to listen before giving up.</param>
    /// <returns>The frames received, in order. Empty when none arrived in the window.</returns>
    /// <remarks>
    /// A test uses this to prove that no frame arrived, for example that a dropped interim
    /// transcript never reaches the model. Silence is a valid outcome on this socket, so a quiet
    /// window returns an empty list instead of throwing. The wait times out on the channel this
    /// class reads from, never on the socket itself, so a quiet window leaves the socket exactly as
    /// usable afterwards as it was before.
    /// </remarks>
    public async Task<List<JsonNode>> ReadFramesForAsync(TimeSpan window)
    {
        List<JsonNode> frames = [];
        using CancellationTokenSource timeout = new(window);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            timeout.Token, TestContext.Current.CancellationToken);

        try
        {
            while (true)
            {
                var item = await ReadItemAsync(linked.Token).ConfigureAwait(false);
                if (item.IsClose)
                {
                    return frames;
                }

                frames.Add(item.Frame!);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return frames;
        }
    }

    /// <summary>Waits for the host to close, and reports how.</summary>
    /// <returns>The close status and its description.</returns>
    public async Task<(WebSocketCloseStatus? Status, string? Description)> ReadCloseAsync()
    {
        while (true)
        {
            var item = await ReadItemAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
            if (item.IsClose)
            {
                return (item.CloseStatus, item.CloseDescription);
            }
        }
    }

    /// <summary>Drops the socket with no close frame, exactly like a call that died.</summary>
    public void Abort() => _socket.Abort();

    /// <summary>Stops the pump and closes the socket.</summary>
    /// <remarks>
    /// The pump is cancelled and awaited first, and only then does this method look at
    /// <see cref="WebSocketState"/>. <see cref="ClientWebSocket.CloseAsync"/> waits for the peer's
    /// close reply by receiving on the same socket, and a receive already in flight on the pump
    /// would collide with that wait. Retiring the pump before that call keeps this the one place a
    /// receive is ever cancelled on purpose, rather than leaving it dangling for a reader to trip
    /// over.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        _pumpCancellation.Cancel();

        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The pump already reported its outcome on the channel; its task result carries nothing
            // a disposing test needs.
        }

        if (_socket.State is WebSocketState.Open)
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
            catch (WebSocketException)
            {
                // The host already went away. That is the end of a call, not a test failure.
            }
        }

        _pumpCancellation.Dispose();
        _socket.Dispose();
    }

    /// <summary>Reads one item off the channel the pump fills.</summary>
    private async Task<RelayItem> ReadItemAsync(CancellationToken cancellationToken)
        => await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Owns every <see cref="ClientWebSocket.ReceiveAsync"/> call on this socket.</summary>
    /// <remarks>
    /// A reader that gives up on a window and a reader that wants the next frame must never race
    /// each other onto the same receive, so exactly one loop, started once, does every receive and
    /// hands finished messages to whichever reader is waiting on the channel.
    /// </remarks>
    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var buffer = new byte[8 * 1024];
                using MemoryStream message = new();

                WebSocketReceiveResult result;
                while (true)
                {
                    result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await _channel.Writer.WriteAsync(
                            RelayItem.OfClose(_socket.CloseStatus, _socket.CloseStatusDescription),
                            cancellationToken).ConfigureAwait(false);
                        _channel.Writer.TryComplete();
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                    if (result.EndOfMessage)
                    {
                        break;
                    }
                }

                var frame = JsonNode.Parse(Encoding.UTF8.GetString(message.ToArray()))!;
                await _channel.Writer.WriteAsync(RelayItem.OfFrame(frame), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            // The channel carries the fault forward, because a reader must see the real cause —
            // a malformed frame from JsonNode.Parse, for one — and not a generic closed-channel
            // exception that hides it. A test author who reads a bad frame from AgentCore needs
            // that failure, not a report that this fake broke.
            _channel.Writer.TryComplete(exception);
        }
    }

    /// <summary>One thing the pump collected off the wire: a frame, or the close handshake.</summary>
    private readonly record struct RelayItem
    {
        private RelayItem(JsonNode? frame, WebSocketCloseStatus? closeStatus, string? closeDescription, bool isClose)
        {
            Frame = frame;
            CloseStatus = closeStatus;
            CloseDescription = closeDescription;
            IsClose = isClose;
        }

        public JsonNode? Frame { get; }

        public WebSocketCloseStatus? CloseStatus { get; }

        public string? CloseDescription { get; }

        public bool IsClose { get; }

        public static RelayItem OfFrame(JsonNode frame) => new(frame, null, null, isClose: false);

        public static RelayItem OfClose(WebSocketCloseStatus? status, string? description)
            => new(null, status, description, isClose: true);
    }
}
