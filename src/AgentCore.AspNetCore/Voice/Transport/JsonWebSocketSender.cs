using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;

namespace AgentCore.AspNetCore.Voice.Transport
{
    /// <summary>
    /// Every send of one duplex JSON-over-WebSocket connection: the write loop, and the close frame.
    /// </summary>
    /// <param name="socket">The accepted socket. The connection that built this sender owns its lifetime.</param>
    /// <param name="closeTimeout">How long the close handshake may take before the socket is aborted.</param>
    /// <param name="connectionToken">
    /// Cancelled once, for any reason the socket is going away: the request aborted, the host is
    /// stopping, or one of the connection's own tasks ended.
    /// </param>
    internal sealed class JsonWebSocketSender(WebSocket socket, TimeSpan closeTimeout, CancellationToken connectionToken)
    {
        /// <summary>Sends whatever the caller queues, one item at a time, until the conversation ends.</summary>
        /// <typeparam name="T">Whatever the caller queues. This sender never looks inside one.</typeparam>
        /// <param name="reader">The queue the caller's own writers fill.</param>
        /// <param name="encode">
        /// Writes one queued item into the loop's own <see cref="Utf8JsonWriter"/>, and returns
        /// <see langword="false"/> to drop it instead. Both halves belong to the caller: the writing is
        /// the adapter's wire format, and the drop is the last barge-in gate, which is expressed in the
        /// output port's own vocabulary and never in this sender's. It runs immediately before the one
        /// send below, which is the whole point of it running here rather than at the point an item was
        /// queued. It is handed the writer rather than asked for an array so that the buffer behind it
        /// can be reused for the life of the loop; it must write one complete JSON value and it must not
        /// keep the writer, flush it, or hold anything the writer wrote past its own return.
        /// </param>
        /// <param name="sendGate">
        /// Held from <paramref name="encode"/> until the send has started, never across an await. A caller that
        /// changes what <paramref name="encode"/> drops under the same lock knows that, once it releases the
        /// lock, no frame the change drops can still start going out.
        /// </param>
        /// <returns>
        /// A task that completes when the queue completes, the socket is gone, or teardown cancels. A send that
        /// finds the socket gone ends the loop without a fault: the read loop and teardown report why it went.
        /// </returns>
        public async Task WriteLoopAsync<T>(ChannelReader<T> reader, Func<T, Utf8JsonWriter, bool> encode, Lock sendGate)
        {
            // One buffer and one writer for the life of the loop, rather than one array per spoken
            // word. The channel is SingleReader, so this loop is the only thing that touches either.
            // Every send is awaited before the next frame resets the buffer, so nothing can hand the
            // same memory to two sends at once — and WrittenMemory never leaves this method, so nothing
            // outside it can still be holding the previous frame's bytes when the reset happens.
            ArrayBufferWriter<byte> buffer = new(256);
            using Utf8JsonWriter writer = new(buffer);

            try
            {
                await foreach (T? item in reader.ReadAllAsync(connectionToken).ConfigureAwait(false))
                {
                    if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
                    {
                        return;
                    }

                    ValueTask sending;
                    lock (sendGate)
                    {
                        // Reset before encode rather than after the send: an item the gate inside encode drops
                        // writes nothing at all, and leaving the reset to the next iteration would then send
                        // the frame before it a second time.
                        buffer.ResetWrittenCount();
                        writer.Reset(buffer);

                        if (!encode(item, writer))
                        {
                            continue;
                        }

                        // The caller writes; this loop flushes. A Utf8JsonWriter holds the tail of a value in
                        // its own buffer until something asks for it, so the send below would otherwise get a
                        // truncated frame rather than a whole one. The buffer is in memory, so this never waits.
                        writer.Flush();

                        sending = socket.SendAsync(
                            buffer.WrittenMemory, WebSocketMessageType.Text, endOfMessage: true, connectionToken);
                    }

                    await sending.ConfigureAwait(false);
                }
            }
            catch (Exception fault) when (IsSocketGone(fault))
            {
                // The peer dropped, or the close already ran: nothing more can be sent, and that is not a fault
                // of this loop. A dropped read or teardown already names how the conversation ended.
            }
        }

        /// <summary>Closes the socket, and never waits on the peer forever to do it.</summary>
        /// <param name="status">The status the peer should see.</param>
        /// <param name="description">Why, or <see langword="null"/> when the status says it all.</param>
        /// <returns>A task that completes once the socket is closed or aborted.</returns>
        public async Task CloseAsync(WebSocketCloseStatus status, string? description)
        {
            if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
            {
                return;
            }

            // Sends serialize behind the WebSocket's own send mutex, so a close that overlaps a send
            // stuck on relay backpressure waits on it rather than throwing — and CancellationToken.None
            // would then wait forever. The connection token is already cancelled by the time this runs,
            // so the close gets its own bounded token instead.
            using CancellationTokenSource closeDeadline = new();
            closeDeadline.CancelAfter(closeTimeout);

            try
            {
                // CloseOutputAsync, never CloseAsync. CloseAsync waits for the peer close frame, and a
                // conversation that already dropped never sends one.
                await socket
                    .CloseOutputAsync(status, TruncateForCloseFrame(description), closeDeadline.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (closeDeadline.IsCancellationRequested)
            {
                // The relay stopped reading and applied backpressure, so the send behind the close
                // would never complete. Abort rather than leave the connection, the Kestrel request,
                // and the store entry alive forever.
                socket.Abort();
            }
            catch (OperationCanceledException) when (socket.State == WebSocketState.Aborted)
            {
                // A send the teardown cancelled aborted the socket under the close: there is nothing left to close.
            }
            catch (WebSocketException)
            {
                // The far end already went away. That is the end of a conversation, not a fault.
            }
        }

        /// <summary>Keeps a close description inside the 123-byte limit the close frame's control payload allows.</summary>
        /// <param name="description">The description a status carries, or null.</param>
        /// <returns>The description, cut short if needed, or null.</returns>
        private static string? TruncateForCloseFrame(string? description)
        {
            return description is { Length: > 100 } ? description[..100] : description;
        }

        /// <summary>Tells a send that failed because the socket went away from a fault of this connection.</summary>
        /// <param name="fault">What the write loop ended with.</param>
        /// <returns><see langword="true"/> when the socket was already unusable, so the loop just ends.</returns>
        private bool IsSocketGone(Exception fault)
        {
            return fault is WebSocketException { WebSocketErrorCode: WebSocketError.ConnectionClosedPrematurely }
                || (fault is WebSocketException or OperationCanceledException
                    && socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived));
        }
    }
}
