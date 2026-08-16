using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Diagnostics;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentCore.Application.Tests.Runtime;

/// <summary>
/// The one seam between the turn loop and everything that watches it.
/// </summary>
/// <remarks>
/// Section 7 measures a durable insert at 13 ms p50 against 91 nanoseconds to enqueue, so the rule
/// these tests fix is that no observer ever sits on the turn: a fast one costs the caller nothing but
/// the call itself, a slow one is watched off-turn, and one that throws is reported and forgotten.
/// The fourth rule is the one the old fire-and-forget code did not have. The chain of D23 is a record
/// of a call, so the facts of one call must reach an observer in the order the call produced them,
/// however long any one of them takes.
/// </remarks>
public sealed class CallObserverDispatcherTests
{
    private const string CallId = "call-1";

    /// <summary>The id <c>Log.AuditAppendFailed</c> carries. It is unchanged by the hook.</summary>
    private const int AuditAppendFailedEventId = 5;

    [Fact]
    public void AnObserverThatAnswersAtOnce_HasTheEventBeforeDispatchReturns()
    {
        var observer = new RecordingObserver();
        var dispatcher = new CallObserverDispatcher([observer]);

        dispatcher.Dispatch(Event(CallEventKind.CallStarted));

        // No await anywhere: a synchronous observer runs on the caller's thread, which is what makes
        // the fast path cost the turn the enqueue and nothing else.
        Assert.Equal([CallEventKind.CallStarted], observer.Seen);
    }

    [Fact]
    public void ManyEventsThroughTheFastPath_ArriveInOrder()
    {
        var observer = new RecordingObserver();
        var dispatcher = new CallObserverDispatcher([observer]);

        dispatcher.Dispatch(Event(CallEventKind.CallStarted));
        dispatcher.Dispatch(Event(CallEventKind.TurnCompleted));
        dispatcher.Dispatch(Event(CallEventKind.CallEnded));

        Assert.Equal(
            [CallEventKind.CallStarted, CallEventKind.TurnCompleted, CallEventKind.CallEnded],
            observer.Seen);
    }

    [Fact]
    public void EveryObserver_ReadsTheSameEvent_InTheOrderTheyWereGiven()
    {
        var first = new RecordingObserver();
        var second = new RecordingObserver();
        var order = new List<string>();
        first.OnEvent = _ => order.Add("first");
        second.OnEvent = _ => order.Add("second");
        var dispatcher = new CallObserverDispatcher([first, second]);

        dispatcher.Dispatch(Event(CallEventKind.ToolFailed));

        Assert.Equal([CallEventKind.ToolFailed], first.Seen);
        Assert.Equal([CallEventKind.ToolFailed], second.Seen);
        Assert.Equal(["first", "second"], order);
    }

    [Fact]
    public void NoObserversAtAll_IsANoOp()
    {
        var dispatcher = new CallObserverDispatcher([]);

        dispatcher.Dispatch(Event(CallEventKind.CallStarted));

        Assert.Equal(0, dispatcher.Count);
    }

    [Fact]
    public async Task AnObserverThatBlocks_DoesNotBlockTheCaller()
    {
        var observer = new GatedObserver();
        var dispatcher = new CallObserverDispatcher([observer]);

        // The observer is inside OnCallEventAsync and will stay there until the test releases it.
        // Dispatch still returns, which is the whole contract of the port.
        dispatcher.Dispatch(Event(CallEventKind.TurnCompleted));

        await observer.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Empty(observer.Seen);

        observer.Release();
        await WaitFor(() => observer.Seen.Count == 1);
    }

    [Fact]
    public async Task TwoSlowEvents_ReachTheObserverInTheOrderTheCallRaisedThem()
    {
        // The old code was `_ = ObserveAppendAsync(...)`, so the second event overtook the first
        // whenever the first was slower. The second gate is released FIRST here, which is exactly
        // the race that used to shuffle the chain.
        var observer = new GatedObserver();
        var dispatcher = new CallObserverDispatcher([observer]);

        dispatcher.Dispatch(Event(CallEventKind.TurnCompleted));
        await observer.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        dispatcher.Dispatch(Event(CallEventKind.CallEnded));

        // The second event is queued behind the first, so the observer has not even been asked about
        // it yet. There is nothing to release.
        Assert.Equal(1, observer.Entries);

        observer.Release();
        await WaitFor(() => observer.Seen.Count == 2);

        Assert.Equal([CallEventKind.TurnCompleted, CallEventKind.CallEnded], observer.Seen);
    }

    [Fact]
    public async Task ASlowObserver_DoesNotDelayALaterEventForAFastOne()
    {
        // Order is a guarantee per observer, and only per observer. A single shared tail would put
        // the counters and the log lines of every later fact behind the sink still writing an earlier
        // row, which is the cost section 8.6 and section 8.7 are not allowed to pay.
        var slow = new GatedObserver();
        var fast = new RecordingObserver();
        var dispatcher = new CallObserverDispatcher([slow, fast]);

        dispatcher.Dispatch(Event(CallEventKind.TurnCompleted));
        await slow.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        // The slow observer is inside OnCallEventAsync for the first event and stays there.
        dispatcher.Dispatch(Event(CallEventKind.CallEnded));

        // No await anywhere between the dispatch and this assertion: the second fact reached the fast
        // observer on the caller's thread while the first one was still open at the slow one.
        Assert.Equal([CallEventKind.TurnCompleted, CallEventKind.CallEnded], fast.Seen);

        // And the slow observer was never asked about the second fact, because its own first one is
        // still open. Its order is kept while it costs nobody else anything.
        Assert.Equal(1, slow.Entries);

        slow.Release();
        await WaitFor(() => slow.Seen.Count == 2);

        Assert.Equal([CallEventKind.TurnCompleted, CallEventKind.CallEnded], slow.Seen);
    }

    [Fact]
    public async Task AQueuedEvent_StillArrivesWhenTheEventBeforeItFailed()
    {
        var observer = new GatedObserver { ThrowsOn = CallEventKind.TurnCompleted };
        var dispatcher = new CallObserverDispatcher([observer]);

        dispatcher.Dispatch(Event(CallEventKind.TurnCompleted));
        await observer.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        dispatcher.Dispatch(Event(CallEventKind.CallEnded));
        observer.Release();

        await WaitFor(() => observer.Seen.Count == 1);
        Assert.Equal([CallEventKind.CallEnded], observer.Seen);
    }

    [Fact]
    public void AnObserverThatThrowsAtOnce_NeitherPropagatesNorCostsTheOthersTheEvent()
    {
        var logger = new RecordingLogger();
        var throwing = new ThrowingObserver();
        var recording = new RecordingObserver();
        var dispatcher = new CallObserverDispatcher([throwing, recording], logger);

        // Audit is a record of the call and never a part of it, so nothing here reaches the turn.
        dispatcher.Dispatch(Event(CallEventKind.CallStarted));

        Assert.Equal([CallEventKind.CallStarted], recording.Seen);

        var line = Assert.Single(logger.Of(AuditAppendFailedEventId));
        Assert.Equal(LogLevel.Error, line.Level);
        Assert.Contains("call.started", line.Message, StringComparison.Ordinal);
        Assert.Contains(CallId, line.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(line.Exception);
    }

    [Fact]
    public async Task AnObserverThatThrowsLongAfterTheTurn_IsReportedAndNothingElseHappens()
    {
        var logger = new RecordingLogger();
        var observer = new GatedObserver { ThrowsOn = CallEventKind.CallEnded };
        var dispatcher = new CallObserverDispatcher([observer], logger);

        dispatcher.Dispatch(Event(CallEventKind.CallEnded));
        await observer.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        observer.Release();
        await WaitFor(() => logger.Of(AuditAppendFailedEventId).Count == 1);

        var line = Assert.Single(logger.Of(AuditAppendFailedEventId));
        Assert.Contains("call.ended", line.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(line.Exception);
    }

    [Fact]
    public void ADiagnosticKind_IsReportedUnderItsOwnToken()
    {
        var logger = new RecordingLogger();
        var dispatcher = new CallObserverDispatcher([new ThrowingObserver()], logger);

        // The four diagnostic kinds reach no chain, so AuditEventKinds knows no token for them. The
        // report still has to name what failed.
        dispatcher.Dispatch(Event(CallEventKind.ExtractionFailed));

        var line = Assert.Single(logger.Of(AuditAppendFailedEventId));
        Assert.Contains("extraction.failed", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailingObserver_DoesNotStopTheNextEvent()
    {
        var throwing = new ThrowingObserver();
        var recording = new RecordingObserver();
        var dispatcher = new CallObserverDispatcher([throwing, recording]);

        dispatcher.Dispatch(Event(CallEventKind.CallStarted));
        dispatcher.Dispatch(Event(CallEventKind.CallEnded));

        Assert.Equal([CallEventKind.CallStarted, CallEventKind.CallEnded], recording.Seen);
    }

    [Fact]
    public void NoEvent_IsRefused()
        => Assert.Throws<ArgumentNullException>(
            () => new CallObserverDispatcher([new RecordingObserver()]).Dispatch(null!));

    private static CallEvent Event(CallEventKind kind) => new()
    {
        CallId = CallId,
        Kind = kind,
        OccurredAt = DateTimeOffset.UnixEpoch,
    };

    /// <summary>Waits for something an observer does off-turn, and fails rather than hanging.</summary>
    private static async Task WaitFor(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(5, deadline.Token);
        }
    }

    /// <summary>
    /// An observer that answers at once and remembers what it read.
    /// </summary>
    private sealed class RecordingObserver : ICallObserver
    {
        private readonly Lock _gate = new();
        private readonly List<CallEventKind> _seen = [];

        /// <summary>Gets what the observer read, oldest first.</summary>
        public IReadOnlyList<CallEventKind> Seen
        {
            get
            {
                lock (_gate)
                {
                    return [.. _seen];
                }
            }
        }

        /// <summary>Runs beside the recording, so a test can watch the order across observers.</summary>
        public Action<CallEvent>? OnEvent { get; set; }

        public ValueTask OnCallEventAsync(CallEvent callEvent, CancellationToken cancellationToken)
        {
            Assert.False(cancellationToken.CanBeCanceled, "The dispatcher passes CancellationToken.None.");

            lock (_gate)
            {
                _seen.Add(callEvent.Kind);
            }

            OnEvent?.Invoke(callEvent);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// An observer that holds every event open until a test releases it.
    /// </summary>
    /// <remarks>
    /// It records AFTER the gate, so <see cref="Seen"/> answers what the observer finished and
    /// <see cref="Entries"/> answers what it was asked about. The difference is what an ordering test
    /// reads.
    /// </remarks>
    private sealed class GatedObserver : ICallObserver
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock _lock = new();
        private readonly List<CallEventKind> _seen = [];
        private int _entries;

        /// <summary>Gets the events the observer accepted, oldest first.</summary>
        public IReadOnlyList<CallEventKind> Seen
        {
            get
            {
                lock (_lock)
                {
                    return [.. _seen];
                }
            }
        }

        /// <summary>Gets the number of times the dispatcher has called the observer.</summary>
        public int Entries => Volatile.Read(ref _entries);

        /// <summary>Set when the dispatcher first calls the observer.</summary>
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The one kind the observer refuses, or <see langword="null"/> when it refuses none.</summary>
        public CallEventKind? ThrowsOn { get; init; }

        /// <summary>Lets every open call complete.</summary>
        public void Release() => _gate.TrySetResult();

        public async ValueTask OnCallEventAsync(CallEvent callEvent, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _entries);
            Entered.TrySetResult();

            await _gate.Task.ConfigureAwait(false);

            if (ThrowsOn == callEvent.Kind)
            {
                throw new InvalidOperationException(ThrowingObserver.Message);
            }

            lock (_lock)
            {
                _seen.Add(callEvent.Kind);
            }
        }
    }

    /// <summary>
    /// An observer that refuses every event before it has awaited anything.
    /// </summary>
    private sealed class ThrowingObserver : ICallObserver
    {
        /// <summary>The message every refusal carries.</summary>
        public const string Message = "the observer is broken.";

        public ValueTask OnCallEventAsync(CallEvent callEvent, CancellationToken cancellationToken)
            => throw new InvalidOperationException(Message);
    }
}
