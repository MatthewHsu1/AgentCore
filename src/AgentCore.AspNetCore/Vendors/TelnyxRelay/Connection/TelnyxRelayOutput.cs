using System.Text.Json;
using System.Threading.Channels;
using AgentCore.AspNetCore.Vendors.TelnyxRelay.Wire;
using AgentCore.AspNetCore.Voice.Ports;

namespace AgentCore.AspNetCore.Vendors.TelnyxRelay.Connection
{
    /// <summary>
    /// The reply half of one relay socket: queues text frames for the write loop, and drops every
    /// frame a barge-in has cut off.
    /// </summary>
    internal sealed class TelnyxRelayOutput : IConversationOutputPort
    {
        // Bounded, so a slow relay slows the turn loop instead of growing a queue without a bound.
        private readonly Channel<OutboundItem> _outbound = Channel.CreateBounded<OutboundItem>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

        private long _replyGeneration;

        // The generation of the reply begun last, fixed when it began. Read and written under _gate.
        private long _openGeneration;

        // Whether a fragment was queued since the last close or stop. Read and written under _gate.
        private bool _replyOpen;

        private readonly Lock _gate = new();

        /// <summary>Gets what the write loop reads.</summary>
        public ChannelReader<OutboundItem> Reader => _outbound.Reader;

        /// <summary>Gets the lock <see cref="StopAsync"/> cuts a reply off under.</summary>
        public Lock SendGate => _gate;

        /// <summary>Ends the queue: the write loop ends once it has sent what is left, and every later write is dropped.</summary>
        public void Complete()
        {
            _ = _outbound.Writer.TryComplete();
        }

        /// <summary>Writes one queued item as the frame to send, or drops it.</summary>
        /// <param name="item">What a reply queued, and the generation it was queued under.</param>
        /// <param name="writer">The write loop's own writer, already reset for this frame.</param>
        /// <returns>
        /// <see langword="true"/> when a whole frame was written, and <see langword="false"/> to drop
        /// <paramref name="item"/> without sending anything.
        /// </returns>
        public bool Encode(OutboundItem item, Utf8JsonWriter writer)
        {
            if (item.Generation is { } generation && Interlocked.Read(ref _replyGeneration) >= generation)
            {
                return false;
            }

            JsonSerializer.Serialize(writer, item.Frame, item.Frame.GetType(), TelnyxRelayJson.Options);

            return true;
        }

        /// <inheritdoc />
        public void BeginReply()
        {
            lock (_gate)
            {
                _openGeneration = CurrentGeneration() + 1;
                _replyOpen = false;
            }
        }

        /// <inheritdoc />
        public ValueTask SpeakAsync(string fragment, CancellationToken cancellationToken = default)
        {
            long generation;
            lock (_gate)
            {
                generation = _openGeneration;
                if (generation <= CurrentGeneration())
                {
                    return ValueTask.CompletedTask;
                }

                _replyOpen = true;
            }

            return WriteAsync(new OutboundItem(generation, new RelayToken(fragment, Last: false)), cancellationToken);
        }

        /// <inheritdoc />
        public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
        {
            // A reply a barge-in stopped has nothing left to close: the speech behind it still flushes as
            // it tears down, and that closing token would reach the vendor after the interrupt.
            long generation;
            lock (_gate)
            {
                if (!_replyOpen)
                {
                    return ValueTask.CompletedTask;
                }

                _replyOpen = false;
                generation = _openGeneration;
            }

            // The vendor closes a reply on last: true, and the sample uses an empty final token when
            // the stream ended with no trailing text.
            return WriteAsync(new OutboundItem(generation, new RelayToken(string.Empty, Last: true)), cancellationToken);
        }

        /// <inheritdoc />
        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _replyOpen = false;
                _ = Interlocked.Increment(ref _replyGeneration);
            }

            while (_outbound.Reader.TryRead(out _))
            {
                // Drained, never sent: the barge-in above already cut off everything still queued.
            }

            return ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        private static async ValueTask DropWhenCompletedAsync(ValueTask write)
        {
            try
            {
                await write.ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                // The socket is gone: nothing more can be sent, and nothing is waiting to hear it.
            }
        }

        private ValueTask WriteAsync(OutboundItem item, CancellationToken cancellationToken)
        {
            ValueTask write = _outbound.Writer.WriteAsync(item, cancellationToken);
            return write.IsCompletedSuccessfully ? write : DropWhenCompletedAsync(write);
        }

        /// <summary>Reads the newest reply generation a barge-in has cut off, or 0 when none has.</summary>
        /// <returns>The counter, read the same way the write loop's own gate reads it.</returns>
        private long CurrentGeneration()
        {
            return Interlocked.Read(ref _replyGeneration);
        }
    }
}
