using System.Buffers;
using System.Net.WebSockets;
using System.Text;

namespace AgentCore.AspNetCore.Vendors.OpenAiLive.Wire
{
    /// <summary>The sideband over a client WebSocket.</summary>
    internal sealed class WebSocketSideband(WebSocket socket) : ILiveSideband
    {
        /// <summary>The largest message accepted. A transcript delta or a delegation is a few hundred bytes.</summary>
        internal const int MaxMessageBytes = 1024 * 1024;

        private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(2);

        internal static async ValueTask<ILiveSideband> ConnectAsync(LiveAttach attach, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(attach);

            ClientWebSocket client = new();
            try
            {
                client.Options.SetRequestHeader("Authorization", "Bearer " + attach.ApiKey);
                await client.ConnectAsync(OpenAiLiveWire.AttachUri(attach.ApiBase, attach.CallId), cancellationToken).ConfigureAwait(false);
                return new WebSocketSideband(client);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        /// <exception cref="InvalidDataException">A message passed <see cref="MaxMessageBytes"/>.</exception>
        public async ValueTask<string?> ReceiveAsync(CancellationToken cancellationToken)
        {
            ArrayBufferWriter<byte> message = new();
            while (true)
            {
                ValueWebSocketReceiveResult result = await socket.ReceiveAsync(message.GetMemory(4096), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                message.Advance(result.Count);
                if (message.WrittenCount > MaxMessageBytes)
                {
                    throw new InvalidDataException($"A sideband message passed {MaxMessageBytes} bytes.");
                }

                if (result.EndOfMessage)
                {
                    return Encoding.UTF8.GetString(message.WrittenSpan);
                }
            }
        }

        public async ValueTask SendAsync(string json, CancellationToken cancellationToken)
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using CancellationTokenSource wait = new(CloseWait);
                try
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, wait.Token).ConfigureAwait(false);
                }
                catch (Exception fault) when (fault is WebSocketException or OperationCanceledException)
                {
                    // The peer is already gone; there is nothing to close.
                }
            }

            socket.Dispose();
        }
    }
}
