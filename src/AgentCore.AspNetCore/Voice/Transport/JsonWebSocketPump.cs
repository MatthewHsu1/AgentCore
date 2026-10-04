using System.Buffers;
using System.Net.WebSockets;
using AgentCore.AspNetCore.Voice.Ports;

namespace AgentCore.AspNetCore.Voice.Transport
{
    /// <summary>
    /// The read loop of one duplex JSON-over-WebSocket connection. <see cref="JsonWebSocketSender"/> is the send half.
    /// </summary>
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

        /// <summary>Reads whole messages off the socket until the conversation ends, and hands each one over.</summary>
        /// <param name="parse">Reads one reassembled message, and never throws on what the peer sent.</param>
        /// <param name="dispatch">
        /// Takes one parsed frame. It is awaited, so a caller that must not block the loop — a turn, for
        /// one — starts its own task and returns.
        /// </param>
        /// <returns>A task that completes when the socket, the idle deadline, or teardown ends the loop.</returns>
        public async Task ReadLoopAsync(FrameParser parse, Func<object, Task> dispatch)
        {
            byte[] rented = ArrayPool<byte>.Shared.Rent(4 * 1024);
            ArrayBufferWriter<byte> message = new(4 * 1024);

            // Set the moment an abandoned receive takes over returning `rented` — see
            // ObserveAbandonedReceive. Once true, the finally below must not also return it: the pool
            // would then hand the same array out twice at once, which corrupts it just as surely as
            // returning it too early does.
            bool bufferOwnedByAbandonedReceive = false;

            try
            {
                while (!connectionToken.IsCancellationRequested)
                {
                    message.ResetWrittenCount();

                    // The vendor never reconnects, so a socket with no inbound frame for IdleTimeout is
                    // a conversation that already ended, not a fault. The first receive of every message races
                    // the deadline rather than being cancelled by one: cancelling ReceiveAsync's own token while it is pending
                    // aborts the socket outright — WebSocketState.Aborted — which then makes the
                    // graceful NormalClosure close below throw instead of reaching the vendor. Racing a
                    // side task leaves the receive itself untouched; the side that loses the race is
                    // simply abandoned rather than cancelled, exactly as a healthy send and a healthy
                    // receive already run concurrently on this same socket during ordinary operation.
                    //
                    // idling is built first, deliberately: Task.Delay validates IdleTimeout and throws
                    // synchronously — confirmed on net10 for a negative span, 60 days, and
                    // TimeSpan.MaxValue — for anything app.MapCall()'s own startup check did not
                    // catch. Building it before receiving exists means that throw can only ever find
                    // no receive in flight yet, so the finally below stays free to return `rented`
                    // unconditionally; building it after would leave a live receive, on
                    // CancellationToken.None, stranded against an array the finally had already handed
                    // back to the pool — the exact hazard fixed elsewhere in this method, reopened by a
                    // bad option value instead of by teardown.
                    using CancellationTokenSource idleCancel = CancellationTokenSource.CreateLinkedTokenSource(connectionToken);
                    Task idling = Task.Delay(options.IdleTimeout, timeProvider, idleCancel.Token);
                    Task<ValueWebSocketReceiveResult> receiving = socket.ReceiveAsync(rented.AsMemory(), CancellationToken.None).AsTask();

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
                    // now instead of leaving one pending per message for as long as a busy conversation keeps
                    // sending faster than IdleTimeout — CancelAfter is never involved, so this touches
                    // nothing outside this one local source.
                    await idleCancel.CancelAsync().ConfigureAwait(false);
                    ValueWebSocketReceiveResult first = await receiving.ConfigureAwait(false);

                    if (!await AssembleMessageAsync(first, rented, message).ConfigureAwait(false))
                    {
                        return;
                    }

                    // Parsed here, synchronously, before any await sees this iteration again. Passing
                    // the parsed frame to dispatch — rather than the bytes, which an async method
                    // cannot take as a ReadOnlySpan<byte> parameter anyway — means nothing async
                    // can ever alias the rented buffer or the ArrayBufferWriter this loop is about to
                    // reset and reuse.
                    FrameOutcome outcome = parse(message.WrittenSpan);

                    if (outcome.Frame is null)
                    {
                        RefuseFrame(outcome);
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

        /// <summary>Collects the rest of one message into <paramref name="message"/>, fragment by fragment.</summary>
        /// <param name="first">The fragment that already won the idle race.</param>
        /// <param name="rented">The array every fragment lands in before it is copied out.</param>
        /// <param name="message">The writer the whole message is assembled into.</param>
        /// <returns>
        /// <see langword="true"/> once the whole message is in <paramref name="message"/>;
        /// <see langword="false"/> when the peer sent a close frame instead.
        /// </returns>
        private async Task<bool> AssembleMessageAsync(
            ValueWebSocketReceiveResult first,
            byte[] rented,
            ArrayBufferWriter<byte> message)
        {
            ValueWebSocketReceiveResult result = first;

            while (true)
            {
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return false;
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
                    return true;
                }

                result = await socket
                    .ReceiveAsync(rented.AsMemory(), connectionToken)
                    .ConfigureAwait(false);
            }
        }

        /// <summary>Logs, or closes the socket over, a message the parser produced no frame for.</summary>
        /// <param name="outcome">An outcome whose <see cref="FrameOutcome.Frame"/> is <see langword="null"/>.</param>
        private void RefuseFrame(FrameOutcome outcome)
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

            // Log once for the conversation, not once for the frame.
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
                // A known type whose body will not bind. A vendor that changes
                // a frame is treated like one that adds a frame: the frame is refused, and the
                // conversation goes on.
                _loggedRefusedFrameBody = true;
                options.LogRefusedFrameBody(outcome.RefusedType!);
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
        private static void ObserveAbandonedReceive(Task<ValueWebSocketReceiveResult> receiving, byte[] buffer)
        {
            _ = receiving.ContinueWith(
                completed =>
                {
                    // Marks a fault observed so it
                    // never becomes an unobserved task exception, whatever receiving finished with.
                    _ = completed.Exception;

                    // clearArray: true. The array still holds the caller's own words, and the pool it
                    // goes back to is shared with Kestrel and with every other read loop in this
                    // process. A pooled buffer that
                    // hands a transcript to the next renter is not a record, it is a leak.
                    ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
