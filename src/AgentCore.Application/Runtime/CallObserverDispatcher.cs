using AgentCore.Application.Diagnostics;
using AgentCore.Application.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.Application.Runtime;

/// <summary>
/// Hands each fact of a call to every observer, in order, and never waits for one.
/// </summary>
/// <remarks>
/// <para>
/// The "never sit on the turn" rule of <see cref="IAuditSinkPort"/> lives here, once, for every
/// observer. Section 7 measures a durable insert at 13 ms at p50 and 32 ms at p99, against 91
/// nanoseconds to enqueue, so an observer that completes synchronously costs the caller the enqueue
/// and nothing else, and an observer that does not is observed on a separate task. The reply leaves
/// while the row is still being written.
/// </para>
/// <para>
/// Nothing here propagates. An observer records the call and is never a part of it, so one that
/// throws is logged once and the turn goes on. That holds for a fault raised before the first await
/// and for a fault raised long after the turn ended, and it holds for each observer separately: a
/// refusing sink does not cost the counters or the log their reading of the same fact.
/// </para>
/// <para>
/// <b>Order is a guarantee, and it is a guarantee per observer.</b> A bare fire-and-forget task per
/// event lets two slow deliveries land out of order, and an audit chain whose events arrive shuffled
/// is not a record of the call. Every observer therefore has a tail of its own in
/// <see cref="_tails"/>, and a dispatch chains onto that observer's tail under <see cref="_gate"/>:
/// the facts of one dispatcher reach ONE observer in the order the call produced them, however long
/// any one of them takes. That is the whole of what the chain of D23 asks for.
/// </para>
/// <para>
/// It is deliberately not one guarantee across observers. A single shared tail would queue every
/// later counter and every later log line behind a sink still writing an earlier row, so the two
/// fast readings would pay the slow one's price on every event after it, for the rest of the call.
/// With a tail each, a slow observer delays only itself, and telemetry and logging keep their old
/// synchronous cost whatever the sink is doing.
/// </para>
/// <para>
/// The lock is held across the synchronous head of every delivery, which is what makes the fast path
/// and the queue one decision rather than a race between two threads. That window is the work an
/// observer does before its first await, and the port already forbids that work from blocking: a
/// counter, a log line, and an enqueue all fit inside it. It is also what fixes the order of those
/// heads, so every observer is still offered a fact in the order the observers were registered.
/// </para>
/// <para>
/// The fast path keeps the old cost exactly. An observer that owes nothing runs on the caller's
/// thread up to its first incomplete await, and a dispatch in which every observer completes
/// synchronously allocates no continuation and touches nothing after the lock. This is what the whole
/// turn loop does when the sink is a queue, which is the shape D23 asks for.
/// </para>
/// <para>
/// One dispatcher belongs to one call, because the ordering guarantee is per dispatcher and a shared
/// instance would make every call queue behind every other. The observers behind it are shared, and
/// they hold no per-call state.
/// </para>
/// </remarks>
internal sealed class CallObserverDispatcher
{
    private readonly ICallObserver[] _observers;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();

    /// <summary>
    /// The delivery each observer's later events queue behind, indexed alongside
    /// <see cref="_observers"/>. An entry is <see langword="null"/> while that observer owes nothing,
    /// which is the fast path. None of these tasks ever faults.
    /// </summary>
    private readonly Task?[] _tails;

    /// <summary>Creates the dispatcher of one call.</summary>
    /// <param name="observers">
    /// Everything that watches the call. Each one takes every fact, in the order they are given here.
    /// An empty set is legal, and it makes every dispatch a no-op.
    /// </param>
    /// <param name="logger">
    /// The logger an observer's fault is reported to, or <see langword="null"/> for
    /// <see cref="NullLogger.Instance"/>. The library never throws for want of one.
    /// </param>
    public CallObserverDispatcher(IEnumerable<ICallObserver> observers, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(observers);

        _observers = [.. observers];
        _tails = new Task?[_observers.Length];
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Gets the number of observers watching the call.</summary>
    public int Count => _observers.Length;

    /// <summary>Hands one fact to every observer, and never waits for them.</summary>
    /// <param name="callEvent">What happened.</param>
    /// <exception cref="ArgumentNullException"><paramref name="callEvent"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// It returns as soon as the observers have accepted the fact, which for a well-behaved observer
    /// is before it returns at all. Nothing an observer does can make this method throw or block.
    /// </remarks>
    public void Dispatch(CallEvent callEvent)
    {
        ArgumentNullException.ThrowIfNull(callEvent);

        if (_observers.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            // The observers are offered the fact one after another rather than all at once, so the
            // order they were registered in is the order they read the call in.
            for (int index = 0; index < _observers.Length; index++)
            {
                Task? tail = _tails[index];

                // This observer owes nothing, so the fact runs here and now, on the caller's thread,
                // up to its first await if it has one. Order is kept for free: there is nothing left
                // for it to follow.
                if (tail is null || tail.IsCompleted)
                {
                    Task delivery = DeliverAsync(_observers[index], callEvent);
                    _tails[index] = delivery.IsCompleted ? null : delivery;
                    continue;
                }

                // An earlier fact is still open at THIS observer. This one waits behind it, so that
                // observer never sees the turns of a call out of the order the call ran them — and
                // every other observer still reads this fact now, whatever the slow one is doing.
                _tails[index] = ContinueAsync(tail, _observers[index], callEvent);
            }
        }
    }

    /// <summary>Waits for the earlier fact of one observer, then delivers this one to it.</summary>
    /// <param name="previous">The delivery of that observer already outstanding.</param>
    /// <param name="observer">The observer both facts belong to.</param>
    /// <param name="callEvent">The fact queued behind the earlier one.</param>
    /// <returns>A task that always completes, and never faults.</returns>
    private async Task ContinueAsync(Task previous, ICallObserver observer, CallEvent callEvent)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A delivery reports its own faults, and a broken one must not stop the next fact.
        catch (Exception)
#pragma warning restore CA1031
        {
            // DeliverAsync swallows everything an observer raises and everything the report of that
            // raises, so nothing is expected here. It is caught all the same, because the fact behind
            // this one is owed to the observer whatever happened to the fact before it.
        }

        await DeliverAsync(observer, callEvent).ConfigureAwait(false);
    }

    /// <summary>Offers one fact to one observer.</summary>
    /// <param name="observer">The observer the fact is owed to.</param>
    /// <param name="callEvent">What happened.</param>
    /// <returns>A task that always completes, and never faults.</returns>
    /// <remarks>
    /// A slow observer delays only the later facts of its own tail, off the turn, where the cost is
    /// nobody's.
    /// </remarks>
    private async Task DeliverAsync(ICallObserver observer, CallEvent callEvent)
    {
        try
        {
            // CancellationToken.None: the record belongs to the call, not to the turn the caller may
            // have cancelled.
            ValueTask pending = observer.OnCallEventAsync(callEvent, CancellationToken.None);
            if (pending.IsCompletedSuccessfully)
            {
                return;
            }

            await pending.ConfigureAwait(false);
        }
#pragma warning disable CA1031 // An observer records the call and is never a part of it.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            Report(callEvent, exception);
        }
    }

    /// <summary>Writes the one line an observer's fault is worth, and never throws.</summary>
    /// <param name="callEvent">The fact the observer refused.</param>
    /// <param name="exception">The cause.</param>
    private void Report(CallEvent callEvent, Exception exception)
    {
        try
        {
            // The token is read from CallEventKinds, which is also what AuditCallObserver and
            // TelemetryCallObserver ask about a kind. One mapping, so a line and a row never
            // disagree about what the observer refused.
            Log.AuditAppendFailed(_logger, callEvent.CallId, CallEventKinds.ToToken(callEvent.Kind), exception);
        }
#pragma warning disable CA1031 // A logger that refuses the report is still not a part of the call.
        catch (Exception)
#pragma warning restore CA1031
        {
            // The report itself failed, and there is nowhere left to report that. A delivery that
            // never faults is what the rest of this class promises, so the promise is kept here.
        }
    }
}
