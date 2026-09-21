using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Diagnostics;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// The one seam between the turn loop and everything that watches it.
    /// </summary>
    /// <remarks>
    /// Section 7 measures a durable insert at 13 ms p50 against 91 nanoseconds to enqueue, so the rule
    /// these tests fix is that no observer ever sits on the turn: a fast one costs the caller nothing but
    /// the conversation itself, a slow one is watched off-turn, and one that throws is reported and forgotten.
    /// The fourth rule is the one the old fire-and-forget code did not have. The chain of D23 is a record
    /// of a conversation, so the facts of one conversation must reach an observer in the order the conversation produced them,
    /// however long any one of them takes.
    /// </remarks>
    public sealed class ConversationObserverDispatcherTests
    {
        private const string ConversationId = "conversation-1";

        /// <summary>The id <c>Log.AuditAppendFailed</c> carries. It is unchanged by the hook.</summary>
        private const int AuditAppendFailedEventId = 5;

        [Fact]
        public void AnObserverThatAnswersAtOnce_HasTheEventBeforeDispatchReturns()
        {
            RecordingObserver observer = new();
            ConversationObserverDispatcher dispatcher = new([observer]);

            dispatcher.Dispatch(Event(ConversationEventKind.ConversationStarted));

            // No await anywhere: a synchronous observer runs on the caller's thread, which is what makes
            // the fast path cost the turn the enqueue and nothing else.
            Assert.Equal([ConversationEventKind.ConversationStarted], observer.Seen);
        }

        [Fact]
        public void ManyEventsThroughTheFastPath_ArriveInOrder()
        {
            RecordingObserver observer = new();
            ConversationObserverDispatcher dispatcher = new([observer]);

            dispatcher.Dispatch(Event(ConversationEventKind.ConversationStarted));
            dispatcher.Dispatch(Event(ConversationEventKind.TurnCompleted));
            dispatcher.Dispatch(Event(ConversationEventKind.ConversationEnded));

            Assert.Equal(
                [ConversationEventKind.ConversationStarted, ConversationEventKind.TurnCompleted, ConversationEventKind.ConversationEnded],
                observer.Seen);
        }

        [Fact]
        public void EveryObserver_ReadsTheSameEvent_InTheOrderTheyWereGiven()
        {
            RecordingObserver first = new();
            RecordingObserver second = new();
            List<string> order = [];
            first.OnEvent = _ => order.Add("first");
            second.OnEvent = _ => order.Add("second");
            ConversationObserverDispatcher dispatcher = new([first, second]);

            dispatcher.Dispatch(Event(ConversationEventKind.ToolFailed));

            Assert.Equal([ConversationEventKind.ToolFailed], first.Seen);
            Assert.Equal([ConversationEventKind.ToolFailed], second.Seen);
            Assert.Equal(["first", "second"], order);
        }

        [Fact]
        public void NoObserversAtAll_IsANoOp()
        {
            ConversationObserverDispatcher dispatcher = new([]);

            dispatcher.Dispatch(Event(ConversationEventKind.ConversationStarted));

            Assert.Equal(0, dispatcher.Count);
        }

        [Fact]
        public async Task AnObserverThatBlocks_DoesNotBlockTheCaller()
        {
            GatedObserver observer = new();
            ConversationObserverDispatcher dispatcher = new([observer]);

            // The observer is inside OnConversationEventAsync and will stay there until the test releases it.
            // Dispatch still returns, which is the whole contract of the port.
            dispatcher.Dispatch(Event(ConversationEventKind.TurnCompleted));

            await observer.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Empty(observer.Seen);

            observer.Release();
            await WaitFor(() => observer.Seen.Count == 1);
        }

        [Fact]
        public async Task TwoSlowEvents_ReachTheObserverInTheOrderTheConversationRaisedThem()
        {
            // The old code was `_ = ObserveAppendAsync(...)`, so the second event overtook the first
            // whenever the first was slower. The second gate is released FIRST here, which is exactly
            // the race that used to shuffle the chain.
            GatedObserver observer = new();
            ConversationObserverDispatcher dispatcher = new([observer]);

            dispatcher.Dispatch(Event(ConversationEventKind.TurnCompleted));
            await observer.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);

            dispatcher.Dispatch(Event(ConversationEventKind.ConversationEnded));

            // The second event is queued behind the first, so the observer has not even been asked about
            // it yet. There is nothing to release.
            Assert.Equal(1, observer.Entries);

            observer.Release();
            await WaitFor(() => observer.Seen.Count == 2);

            Assert.Equal([ConversationEventKind.TurnCompleted, ConversationEventKind.ConversationEnded], observer.Seen);
        }

        [Fact]
        public async Task ASlowObserver_DoesNotDelayALaterEventForAFastOne()
        {
            // Order is a guarantee per observer, and only per observer. A single shared tail would put
            // the counters and the log lines of every later fact behind the sink still writing an earlier
            // row, which is the cost section 8.6 and section 8.7 are not allowed to pay.
            GatedObserver slow = new();
            RecordingObserver fast = new();
            ConversationObserverDispatcher dispatcher = new([slow, fast]);

            dispatcher.Dispatch(Event(ConversationEventKind.TurnCompleted));
            await slow.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);

            // The slow observer is inside OnConversationEventAsync for the first event and stays there.
            dispatcher.Dispatch(Event(ConversationEventKind.ConversationEnded));

            // No await anywhere between the dispatch and this assertion: the second fact reached the fast
            // observer on the caller's thread while the first one was still open at the slow one.
            Assert.Equal([ConversationEventKind.TurnCompleted, ConversationEventKind.ConversationEnded], fast.Seen);

            // And the slow observer was never asked about the second fact, because its own first one is
            // still open. Its order is kept while it costs nobody else anything.
            Assert.Equal(1, slow.Entries);

            slow.Release();
            await WaitFor(() => slow.Seen.Count == 2);

            Assert.Equal([ConversationEventKind.TurnCompleted, ConversationEventKind.ConversationEnded], slow.Seen);
        }

        [Fact]
        public async Task AQueuedEvent_StillArrivesWhenTheEventBeforeItFailed()
        {
            GatedObserver observer = new() { ThrowsOn = ConversationEventKind.TurnCompleted };
            ConversationObserverDispatcher dispatcher = new([observer]);

            dispatcher.Dispatch(Event(ConversationEventKind.TurnCompleted));
            await observer.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);

            dispatcher.Dispatch(Event(ConversationEventKind.ConversationEnded));
            observer.Release();

            await WaitFor(() => observer.Seen.Count == 1);
            Assert.Equal([ConversationEventKind.ConversationEnded], observer.Seen);
        }

        [Fact]
        public void AnObserverThatThrowsAtOnce_NeitherPropagatesNorCostsTheOthersTheEvent()
        {
            RecordingLogger logger = new();
            ThrowingObserver throwing = new();
            RecordingObserver recording = new();
            ConversationObserverDispatcher dispatcher = new([throwing, recording], logger);

            // Audit is a record of the conversation and never a part of it, so nothing here reaches the turn.
            dispatcher.Dispatch(Event(ConversationEventKind.ConversationStarted));

            Assert.Equal([ConversationEventKind.ConversationStarted], recording.Seen);

            LogLine line = Assert.Single(logger.Of(AuditAppendFailedEventId));
            Assert.Equal(LogLevel.Error, line.Level);
            Assert.Contains("conversation.started", line.Message, StringComparison.Ordinal);
            Assert.Contains(ConversationId, line.Message, StringComparison.Ordinal);
            _ = Assert.IsType<InvalidOperationException>(line.Exception);
        }

        [Fact]
        public async Task AnObserverThatThrowsLongAfterTheTurn_IsReportedAndNothingElseHappens()
        {
            RecordingLogger logger = new();
            GatedObserver observer = new() { ThrowsOn = ConversationEventKind.ConversationEnded };
            ConversationObserverDispatcher dispatcher = new([observer], logger);

            dispatcher.Dispatch(Event(ConversationEventKind.ConversationEnded));
            await observer.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);

            observer.Release();
            await WaitFor(() => logger.Of(AuditAppendFailedEventId).Count == 1);

            LogLine line = Assert.Single(logger.Of(AuditAppendFailedEventId));
            Assert.Contains("conversation.ended", line.Message, StringComparison.Ordinal);
            _ = Assert.IsType<InvalidOperationException>(line.Exception);
        }

        [Fact]
        public void ADiagnosticKind_IsReportedUnderItsOwnToken()
        {
            RecordingLogger logger = new();
            ConversationObserverDispatcher dispatcher = new([new ThrowingObserver()], logger);

            // The four diagnostic kinds reach no chain, so AuditEventKinds knows no token for them. The
            // report still has to name what failed.
            dispatcher.Dispatch(Event(ConversationEventKind.ExtractionFailed));

            LogLine line = Assert.Single(logger.Of(AuditAppendFailedEventId));
            Assert.Contains("extraction.failed", line.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AFailingObserver_DoesNotStopTheNextEvent()
        {
            ThrowingObserver throwing = new();
            RecordingObserver recording = new();
            ConversationObserverDispatcher dispatcher = new([throwing, recording]);

            dispatcher.Dispatch(Event(ConversationEventKind.ConversationStarted));
            dispatcher.Dispatch(Event(ConversationEventKind.ConversationEnded));

            Assert.Equal([ConversationEventKind.ConversationStarted, ConversationEventKind.ConversationEnded], recording.Seen);
        }

        [Fact]
        public void NoEvent_IsRefused()
        {
            _ = Assert.Throws<ArgumentNullException>(
                        () => new ConversationObserverDispatcher([new RecordingObserver()]).Dispatch(null!));
        }

        private static ConversationEvent Event(ConversationEventKind kind)
        {
            return new()
            {
                ConversationId = ConversationId,
                Kind = kind,
                OccurredAt = DateTimeOffset.UnixEpoch,
            };
        }

        /// <summary>Waits for something an observer does off-turn, and fails rather than hanging.</summary>
        private static async Task WaitFor(Func<bool> condition)
        {
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
            while (!condition())
            {
                await Task.Delay(5, deadline.Token);
            }
        }

        /// <summary>
        /// An observer that answers at once and remembers what it read.
        /// </summary>
        private sealed class RecordingObserver : IConversationObserver
        {
            private readonly Lock _gate = new();
            private readonly List<ConversationEventKind> _seen = [];

            /// <summary>Gets what the observer read, oldest first.</summary>
            public IReadOnlyList<ConversationEventKind> Seen
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
            public Action<ConversationEvent>? OnEvent { get; set; }

            public ValueTask OnConversationEventAsync(ConversationEvent conversationEvent, CancellationToken cancellationToken)
            {
                Assert.False(cancellationToken.CanBeCanceled, "The dispatcher passes CancellationToken.None.");

                lock (_gate)
                {
                    _seen.Add(conversationEvent.Kind);
                }

                OnEvent?.Invoke(conversationEvent);
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
        private sealed class GatedObserver : IConversationObserver
        {
            private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly Lock _lock = new();
            private readonly List<ConversationEventKind> _seen = [];
            private int _entries;

            /// <summary>Gets the events the observer accepted, oldest first.</summary>
            public IReadOnlyList<ConversationEventKind> Seen
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
            public ConversationEventKind? ThrowsOn { get; init; }

            /// <summary>Lets every open conversation complete.</summary>
            public void Release()
            {
                _ = _gate.TrySetResult();
            }

            public async ValueTask OnConversationEventAsync(ConversationEvent conversationEvent, CancellationToken cancellationToken)
            {
                _ = Interlocked.Increment(ref _entries);
                _ = Entered.TrySetResult();

                await _gate.Task.ConfigureAwait(false);

                if (ThrowsOn == conversationEvent.Kind)
                {
                    throw new InvalidOperationException(ThrowingObserver.Message);
                }

                lock (_lock)
                {
                    _seen.Add(conversationEvent.Kind);
                }
            }
        }

        /// <summary>
        /// An observer that refuses every event before it has awaited anything.
        /// </summary>
        private sealed class ThrowingObserver : IConversationObserver
        {
            /// <summary>The message every refusal carries.</summary>
            public const string Message = "the observer is broken.";

            public ValueTask OnConversationEventAsync(ConversationEvent conversationEvent, CancellationToken cancellationToken)
            {
                throw new InvalidOperationException(Message);
            }
        }
    }
}
