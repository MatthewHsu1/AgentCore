using System.Threading.Channels;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Ports;
using AgentCore.Domain.Audit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.Application.Audit;

/// <summary>
/// Puts a bounded queue and a batching background writer in front of any other audit sink.
/// </summary>
public sealed class QueuedAuditSink : IAuditSinkPort, IAsyncDisposable, IDisposable
{
    /// <summary>The number of events the queue holds before it starts dropping.</summary>
    public const int DefaultCapacity = 10_000;

    /// <summary>The most events the writer hands the store in one call.</summary>
    public const int DefaultBatchSize = 100;

    /// <summary>How long <see cref="Dispose"/> waits for the drain before it gives up.</summary>
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(5);

    private readonly IAuditSinkPort _inner;

    private readonly ILogger _logger;

    private readonly Channel<AuditEvent> _queue;

    private readonly CancellationTokenSource _stopping = new();

    private readonly Task _writer;

    private readonly int _batchSize;

    /// <summary>The events accepted and not yet handed to the store. <see cref="FlushAsync"/> reads it.</summary>
    private int _pending;

    /// <summary>Gets the store this queue writes into.</summary>
    public IAuditSinkPort Store => _inner;

    private int _closed;

    /// <summary>Puts a queue in front of one sink.</summary>
    /// <param name="inner">The store the writer hands each batch to. It may block and it may be slow.</param>
    /// <param name="logger">
    /// The logger a drop and a refused batch are reported to, or <see langword="null"/> for
    /// <see cref="NullLogger.Instance"/>. The library never throws for want of one.
    /// </param>
    /// <param name="capacity">
    /// The number of events the queue holds before it drops, or <see cref="DefaultCapacity"/>.
    /// </param>
    /// <param name="batchSize">
    /// The most events one call to the store carries, or <see cref="DefaultBatchSize"/>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="capacity"/> or <paramref name="batchSize"/> is less than one.
    /// </exception>
    public QueuedAuditSink(
        IAuditSinkPort inner,
        ILogger? logger = null,
        int capacity = DefaultCapacity,
        int batchSize = DefaultBatchSize)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        _inner = inner;

        _logger = logger ?? NullLogger.Instance;

        _batchSize = batchSize;

        _queue = Channel.CreateBounded<AuditEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

        _writer = Task.Run(WriteAsync);
    }

    /// <inheritdoc />
    public ValueTask AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        // Counted before the write, so FlushAsync never reads zero while an event is in the channel.
        Interlocked.Increment(ref _pending);

        if (_queue.Writer.TryWrite(auditEvent))
        {
            return ValueTask.CompletedTask;
        }

        // Either the queue is full or the sink is disposed. Both are a drop, and both are reported:
        // the caller is a turn loop, and a turn is never told that its record was lost.
        Interlocked.Decrement(ref _pending);

        Report(auditEvent);

        return ValueTask.CompletedTask;
    }

    /// <summary>Waits until every accepted event has been handed to the store.</summary>
    /// <param name="cancellationToken">Stops waiting. It cancels no write.</param>
    /// <returns>A task that completes when nothing is left in the queue.</returns>
    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        while (Volatile.Read(ref _pending) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromMilliseconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Stops taking events and writes what is queued.</summary>
    /// <returns>A task that completes when the drain is done.</returns>
    public async ValueTask DisposeAsync()
    {
        // A container captures this once per service type it is registered under, and it answers to
        // two. Without this guard the second close cancels a CancellationTokenSource the first one
        // already disposed.
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        // Completing the writer is what ends the drain loop, and it is also what makes every later
        // TryWrite return false. The queue keeps whatever is already in it, so the drain is honest.
        _queue.Writer.TryComplete();

        // Bounded on purpose. A host that stops is never held by a store that will not answer, and a
        // store that does answer gets the whole window to write what it already accepted.
        if (!await DrainedAsync().ConfigureAwait(false))
        {
            await _stopping.CancelAsync().ConfigureAwait(false);
            await DrainedAsync().ConfigureAwait(false);
        }

        _stopping.Dispose();
    }

    /// <summary>Stops taking events and writes what is queued, blocking until it is done.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>Waits out the drain, and reports whether it finished rather than throwing.</summary>
    /// <returns><see langword="true"/> when the writer loop ended inside <see cref="DisposeTimeout"/>.</returns>
    private async Task<bool> DrainedAsync()
    {
        try
        {
            await _writer.WaitAsync(DisposeTimeout).ConfigureAwait(false);
            return true;
        }
#pragma warning disable CA1031 // A drain that failed has already reported itself, and disposal never throws.
        catch (Exception)
#pragma warning restore CA1031
        {
            // WriteAsync swallows everything the store raises, so a fault here is the timeout.
            return false;
        }
    }

    /// <summary>Drains the queue into the store, in batches, until the queue is complete.</summary>
    /// <returns>A task that always completes, and never faults.</returns>
    private async Task WriteAsync()
    {
        List<AuditEvent> batch = new(_batchSize);

        while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            // Everything waiting goes in one batch, so the store pays one round trip for a burst
            // rather than one for each event of it.
            while (batch.Count < _batchSize && _queue.Reader.TryRead(out AuditEvent? auditEvent))
            {
                batch.Add(auditEvent);
            }

            if (batch.Count == 0)
            {
                continue;
            }

            await AppendBatchAsync(batch).ConfigureAwait(false);
            batch.Clear();
        }
    }

    /// <summary>Hands one batch to the store, and reports whatever it refuses.</summary>
    /// <param name="batch">The events to write, in the order they were accepted.</param>
    /// <returns>A task that always completes, and never faults.</returns>
    private async Task AppendBatchAsync(List<AuditEvent> batch)
    {
        try
        {
            await _inner.AppendManyAsync([.. batch], _stopping.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The store is a record of the call and never a part of it.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            Report(batch, exception);
        }
        finally
        {
            // Counted down whatever happened. A batch the store refused is not pending any more: it
            // is lost, and FlushAsync must not wait for a row that will never be written.
            Interlocked.Add(ref _pending, -batch.Count);
        }
    }

    /// <summary>Writes the one line a dropped event is worth, and never throws.</summary>
    /// <param name="auditEvent">The event the queue had no room for.</param>
    private void Report(AuditEvent auditEvent)
    {
        try
        {
            Log.AuditQueueFull(_logger, auditEvent.CallId, auditEvent.Sequence);
        }
#pragma warning disable CA1031 // A logger that refuses the report is still not a part of the call.
        catch (Exception)
#pragma warning restore CA1031
        {
            // The report itself failed, and there is nowhere left to report that.
        }
    }

    /// <summary>Writes the one line a refused batch is worth, and never throws.</summary>
    /// <param name="batch">The events the store did not take.</param>
    /// <param name="exception">The cause.</param>
    private void Report(List<AuditEvent> batch, Exception exception)
    {
        try
        {
            // One line for the batch and not one for each event. The store refused the call, so the
            // fault is one fault, and the first event names the call an operator goes looking at.
            Log.AuditAppendFailed(
                _logger,
                batch[0].CallId,
                AuditEventKinds.ToToken(batch[0].Kind),
                exception);
        }
#pragma warning disable CA1031 // A logger that refuses the report is still not a part of the call.
        catch (Exception)
#pragma warning restore CA1031
        {
            // The report itself failed, and there is nowhere left to report that.
        }
    }
}
