using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;

namespace AgentCore.AspNetCore.Speech;

/// <summary>What reading one inbound message produced.</summary>
/// <param name="Frame">The parsed frame, or <see langword="null"/> when none was read.</param>
/// <param name="UnknownType">The discriminator no case matched, or <see langword="null"/>.</param>
/// <param name="RefusedType">The known discriminator whose body would not bind, or <see langword="null"/>.</param>
/// <remarks>
/// All three <see langword="null"/> is the only unreadable case, and the only one a caller may
/// close the socket over.
/// </remarks>
internal readonly record struct FrameOutcome(object? Frame, string? UnknownType, string? RefusedType);

/// <summary>Reads one reassembled inbound message into a <see cref="FrameOutcome"/>.</summary>
/// <param name="utf8">The whole message, already reassembled.</param>
/// <returns>What the adapter's own reader made of those bytes.</returns>
/// <remarks>
/// A delegate of its own, rather than a <see cref="Func{T, TResult}"/>: a ref struct cannot be a
/// type argument, and the span is the whole point — it is what keeps the pooled buffer behind the
/// message from ever being aliased by something async.
/// </remarks>
internal delegate FrameOutcome FrameParser(ReadOnlySpan<byte> utf8);

/// <summary>What one pump may do, and for how long.</summary>
/// <param name="MaxFrameBytes">The largest inbound message the pump accepts, in bytes.</param>
/// <param name="IdleTimeout">How long the pump waits with no inbound message before it ends the call.</param>
/// <param name="CloseTimeout">How long the close handshake may take before the socket is aborted.</param>
/// <param name="ProtocolFault">
/// Builds the exception the pump throws when the peer broke the contract. The adapter supplies it,
/// so the pump raises the adapter's own exception type and the adapter's own close-status rules
/// still recognise it.
/// </param>
/// <param name="LogUnknownFrameType">
/// Logs a discriminator no case matched. The pump calls it once for the call, not once for the
/// frame, and passes the name; the line itself names the vendor, which is why it is a callback.
/// </param>
/// <param name="LogRefusedFrameBody">
/// Logs a known discriminator whose body would not bind, on the same once-for-the-call rule as
/// <paramref name="LogUnknownFrameType"/>.
/// </param>
/// <param name="LogIdleTimeout">Logs that the idle deadline, rather than teardown, ended the call.</param>
internal sealed record JsonWebSocketPumpOptions(
    int MaxFrameBytes,
    TimeSpan IdleTimeout,
    TimeSpan CloseTimeout,
    Func<WebSocketCloseStatus, string, Exception> ProtocolFault,
    Action<string> LogUnknownFrameType,
    Action<string> LogRefusedFrameBody,
    Action LogIdleTimeout);

/// <summary>
/// The read loop, the write loop, and the close of one duplex JSON-over-WebSocket connection.
/// </summary>
/// <remarks>
/// <para>
/// Moved out of the Telnyx connection with its comments intact. Every duplex speech adapter reads
/// one message at a time off a socket that fragments where it likes, sends from exactly one task
/// because a WebSocket supports one send at a time, and closes on a bounded deadline — and none of
/// that names a vendor. The three seams that would have are delegates: the parse, the exception
/// factory, and the log callbacks.
/// </para>
/// <para>
/// Nothing here decides what a message means or what a reply is worth sending. The parse belongs to
/// the adapter's own reader, and so does the gate in front of the write loop's one send: an item
/// that a barge-in already cut off is stale in the output port's vocabulary, not the transport's,
/// so <see cref="WriteLoopAsync{T}"/> only asks its caller to write the bytes and drops whatever the
/// caller declines to write.
/// </para>
/// </remarks>
/// <param name="socket">The accepted socket. The connection that built this pump owns its lifetime.</param>
/// <param name="options">What this pump may do, and for how long.</param>
/// <param name="timeProvider">The clock the idle deadline reads.</param>
/// <param name="connectionToken">
/// Cancelled once, for any reason the socket is going away: the request aborted, the host is
/// stopping, or one of the connection's own tasks ended.
/// </param>
internal sealed class JsonWebSocketPump(
    WebSocket socket,
    JsonWebSocketPumpOptions options,
    TimeProvider timeProvider,
    CancellationToken connectionToken)
{
    private bool _loggedUnknownFrame;
    private bool _loggedRefusedFrameBody;

    /// <summary>Reads whole messages off the socket until the call ends, and hands each one over.</summary>
    /// <param name="parse">Reads one reassembled message, and never throws on what the peer sent.</param>
    /// <param name="dispatch">
    /// Takes one parsed frame. It is awaited, so a caller that must not block the loop — a turn, for
    /// one — starts its own task and returns.
    /// </param>
    /// <returns>A task that completes when the socket, the idle deadline, or teardown ends the loop.</returns>
    /// <remarks>
    /// Returns rather than throws for every ordinary end of a call, including the idle deadline, so
    /// a caller reading <see cref="Task.IsFaulted"/> for its close status sees a fault only when
    /// something actually went wrong.
    /// </remarks>
    public async Task ReadLoopAsync(FrameParser parse, Func<object, Task> dispatch)
    {
        var rented = ArrayPool<byte>.Shared.Rent(4 * 1024);
        ArrayBufferWriter<byte> message = new(4 * 1024);

        // Set the moment an abandoned receive takes over returning `rented` — see
        // ObserveAbandonedReceive. Once true, the finally below must not also return it: the pool
        // would then hand the same array out twice at once, which corrupts it just as surely as
        // returning it too early does.
        var bufferOwnedByAbandonedReceive = false;

        try
        {
            while (!connectionToken.IsCancellationRequested)
            {
                message.ResetWrittenCount();
                ValueWebSocketReceiveResult result;

                // The vendor never reconnects, so a socket with no inbound frame for IdleTimeout is
                // a call that already ended, not a fault. The first receive of every message races
                // the deadline rather than being cancelled by one: a probe against a real Kestrel
                // WebSocket confirmed that cancelling ReceiveAsync's own token while it is pending
                // aborts the socket outright — WebSocketState.Aborted — which then makes the
                // graceful NormalClosure close below throw instead of reaching the vendor. Racing a
                // side task leaves the receive itself untouched; the side that loses the race is
                // simply abandoned rather than cancelled, exactly as a healthy send and a healthy
                // receive already run concurrently on this same socket during ordinary operation.
                //
                // idling is built first, deliberately: Task.Delay validates IdleTimeout and throws
                // synchronously — confirmed on net10 for a negative span, 60 days, and
                // TimeSpan.MaxValue — for anything MapTelnyxRelay's own startup check did not
                // catch. Building it before receiving exists means that throw can only ever find
                // no receive in flight yet, so the finally below stays free to return `rented`
                // unconditionally; building it after would leave a live receive, on
                // CancellationToken.None, stranded against an array the finally had already handed
                // back to the pool — the exact hazard fixed elsewhere in this method, reopened by a
                // bad option value instead of by teardown.
                using CancellationTokenSource idleCancel = CancellationTokenSource.CreateLinkedTokenSource(connectionToken);
                var idling = Task.Delay(options.IdleTimeout, timeProvider, idleCancel.Token);
                var receiving = socket.ReceiveAsync(rented.AsMemory(), CancellationToken.None).AsTask();

                if (await Task.WhenAny(receiving, idling).ConfigureAwait(false) != receiving)
                {
                    // idling won: either IdleTimeout actually elapsed, or the connection token
                    // itself fired and cancelled this Task.Delay along with it — host stopping, or
                    // the request aborting. IsCompletedSuccessfully tells the two apart, since a
                    // cancelled Task.Delay never reaches that state, and only the elapsed case is
                    // this connection's own idle deadline rather than teardown asked for elsewhere.
                    // Either way, DetermineCloseStatus only reaches InternalServerError off
                    // reading.IsFaulted, and returning here rather than throwing leaves this task
                    // Completed, not Faulted, so both fall through its checks to the same
                    // NormalClosure a clean close already gets — or to EndpointUnavailable, when
                    // the host is the one stopping.
                    bufferOwnedByAbandonedReceive = true;
                    ObserveAbandonedReceive(receiving, rented);

                    if (idling.IsCompletedSuccessfully)
                    {
                        ConnectionTaskObserver.SafeLog(options.LogIdleTimeout);
                    }

                    return;
                }

                // The receive won the race. Cancelling idling's own token here, rather than
                // leaving it to expire on its own after IdleTimeout, releases the timer behind it
                // now instead of leaving one pending per message for as long as a busy call keeps
                // sending faster than IdleTimeout — CancelAfter is never involved, so this touches
                // nothing outside this one local source.
                idleCancel.Cancel();
                result = await receiving.ConfigureAwait(false);

                // The rest of one message, however many more fragments the vendor chose. A loop
                // that parsed each receive on its own would fail on a fragment, and it would cut a
                // multi-byte character in half. Only the first fragment above raced the idle
                // deadline: the vendor is, by definition, no longer silent once one fragment of a
                // message has already arrived.
                while (true)
                {
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    if (message.WrittenCount + result.Count > options.MaxFrameBytes)
                    {
                        throw options.ProtocolFault(
                            WebSocketCloseStatus.MessageTooBig,
                            "the relay frame passes the size limit.");
                    }

                    message.Write(rented.AsSpan(0, result.Count));

                    if (result.EndOfMessage)
                    {
                        break;
                    }

                    result = await socket
                        .ReceiveAsync(rented.AsMemory(), connectionToken)
                        .ConfigureAwait(false);
                }

                // Parsed here, synchronously, before any await sees this iteration again. Passing
                // the parsed frame to dispatch — rather than the bytes, which an async method
                // cannot take as a ReadOnlySpan<byte> parameter anyway — means nothing async
                // can ever alias the rented buffer or the ArrayBufferWriter this loop is about to
                // reset and reuse.
                var outcome = parse(message.WrittenSpan);

                if (outcome.Frame is null)
                {
                    if (outcome.UnknownType is null && outcome.RefusedType is null)
                    {
                        // Neither name is set, so the bytes carried no readable type at all: not
                        // JSON, not an object, or a type that is not a string. That is the only
                        // shape this endpoint closes a socket over.
                        throw options.ProtocolFault(
                            WebSocketCloseStatus.InvalidPayloadData,
                            "the relay sent a frame this endpoint cannot parse.");
                    }

                    // Log once for the call, not once for the frame. Section 3.1.
                    if (outcome.UnknownType is { } unmodelled)
                    {
                        if (!_loggedUnknownFrame)
                        {
                            _loggedUnknownFrame = true;
                            options.LogUnknownFrameType(unmodelled);
                        }
                    }
                    else if (!_loggedRefusedFrameBody)
                    {
                        // A known type whose body will not bind. Section 7.1 treats a vendor that
                        // changes a frame exactly as it treats one that adds a frame: the frame is
                        // refused, and the call goes on.
                        _loggedRefusedFrameBody = true;
                        options.LogRefusedFrameBody(outcome.RefusedType!);
                    }

                    continue;
                }

                await dispatch(outcome.Frame).ConfigureAwait(false);
            }
        }
        finally
        {
            // Skipped when an abandoned receive is still live against `rented`: ObserveAbandonedReceive
            // owns the return in that case, deferred until that receive itself finishes, and this
            // method has no way to know that has happened yet — returning here too would hand the
            // same array to a second renter while the first receive can still write into it.
            if (!bufferOwnedByAbandonedReceive)
            {
                // clearArray: true, for the reason ObserveAbandonedReceive gives: this array holds
                // what the caller said, and the pool behind it is shared with the whole process.
                ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            }
        }
    }

    /// <summary>Sends whatever the caller queues, one item at a time, until the call ends.</summary>
    /// <typeparam name="T">Whatever the caller queues. This pump never looks inside one.</typeparam>
    /// <param name="reader">The queue the caller's own writers fill.</param>
    /// <param name="encode">
    /// Writes one queued item into the loop's own <see cref="Utf8JsonWriter"/>, and returns
    /// <see langword="false"/> to drop it instead. Both halves belong to the caller: the writing is
    /// the adapter's wire format, and the drop is the last barge-in gate, which is expressed in the
    /// output port's own vocabulary and never in this pump's. It runs immediately before the one
    /// send below, which is the whole point of it running here rather than at the point an item was
    /// queued. It is handed the writer rather than asked for an array so that the buffer behind it
    /// can be reused for the life of the loop; it must write one complete JSON value and it must not
    /// keep the writer, flush it, or hold anything the writer wrote past its own return.
    /// </param>
    /// <returns>A task that completes when the queue completes, the socket closes, or teardown cancels.</returns>
    public async Task WriteLoopAsync<T>(ChannelReader<T> reader, Func<T, Utf8JsonWriter, bool> encode)
    {
        // One buffer and one writer for the life of the loop, rather than one array per spoken
        // word. The channel is SingleReader, so this loop is the only thing that touches either.
        // Every send is awaited before the next frame resets the buffer, so nothing can hand the
        // same memory to two sends at once — and WrittenMemory never leaves this method, so nothing
        // outside it can still be holding the previous frame's bytes when the reset happens.
        ArrayBufferWriter<byte> buffer = new(256);
        using Utf8JsonWriter writer = new(buffer);

        await foreach (var item in reader.ReadAllAsync(connectionToken).ConfigureAwait(false))
        {
            if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
            {
                return;
            }

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
            // truncated frame rather than a whole one.
            writer.Flush();

            await socket
                .SendAsync(buffer.WrittenMemory, WebSocketMessageType.Text, endOfMessage: true, connectionToken)
                .ConfigureAwait(false);
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
        closeDeadline.CancelAfter(options.CloseTimeout);

        try
        {
            // CloseOutputAsync, never CloseAsync. CloseAsync waits for the peer close frame, and a
            // call that already dropped never sends one.
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
        catch (WebSocketException)
        {
            // The far end already went away. That is the end of a call, not a fault.
        }
    }

    /// <summary>Waits out the losing side of the idle race, then hands its buffer back to the pool.</summary>
    /// <param name="receiving">The receive the idle deadline, or teardown, won the race against.</param>
    /// <param name="buffer">
    /// The array <paramref name="receiving"/> still writes into until it completes. Ownership of
    /// returning it to <see cref="ArrayPool{T}.Shared"/> moves here, and here only, the moment
    /// <see cref="ReadLoopAsync"/> abandons this receive — <c>ReadLoopAsync</c>'s own <c>finally</c>
    /// must not also return it, or the pool would see it twice.
    /// </param>
    /// <remarks>
    /// <para>
    /// Nothing here awaits <paramref name="receiving"/> inline, and nothing may: the socket is
    /// about to receive a graceful close, and blocking on a receive that is still pending against
    /// a socket this connection no longer owns would either wait on a peer that already stopped
    /// sending or throw once the close races it.
    /// </para>
    /// <para>
    /// The buffer is returned from this continuation, never from <c>ReadLoopAsync</c>'s own
    /// <c>finally</c>, because that <c>finally</c> runs the instant <c>ReadLoopAsync</c> returns —
    /// while <paramref name="receiving"/> can still be mid-write into <paramref name="buffer"/>. A
    /// probe with two real sockets over a loopback TCP pair proved exactly that: returning early
    /// let a second renter receive the same array, and the first receive's own bytes then landed
    /// in it once the peer actually sent something, overwriting whatever the second renter had put
    /// there. <see cref="ArrayPool{T}.Shared"/> is shared with every other read loop in this
    /// process, including one on a completely different call, so that corruption is not confined
    /// to this connection. Deferring the return until <paramref name="receiving"/> itself finishes
    /// — successfully, faulted, or cancelled, it makes no difference — is what keeps the array out
    /// of the pool for exactly as long as something can still write to it, never shorter.
    /// </para>
    /// </remarks>
    private static void ObserveAbandonedReceive(Task<ValueWebSocketReceiveResult> receiving, byte[] buffer)
    {
        _ = receiving.ContinueWith(
            completed =>
            {
                // Marks a fault observed the same way the earlier, buffer-free version did, so it
                // never becomes an unobserved task exception, whatever receiving finished with.
                _ = completed.Exception;

                // clearArray: true. The array still holds the caller's own words, and the pool it
                // goes back to is shared with Kestrel and with every other read loop in this
                // process. D23 makes the audit chain the record of a call; a pooled buffer that
                // hands a transcript to the next renter is not a record, it is a leak.
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Keeps a close description inside the 123-byte limit the close frame's control payload allows.</summary>
    /// <param name="description">The description a status carries, or null.</param>
    /// <returns>The description, cut short if needed, or null.</returns>
    /// <remarks>
    /// Every description this class ever passes is one of the two fixed, short, plain-English
    /// messages the protocol faults above carry today, so a character cut is a safe
    /// stand-in for a byte-accurate one — this only guards a future message that grows past the
    /// limit, so <see cref="WebSocket.CloseOutputAsync"/> never throws instead of closing.
    /// </remarks>
    private static string? TruncateForCloseFrame(string? description)
        => description is { Length: > 100 } ? description[..100] : description;
}
