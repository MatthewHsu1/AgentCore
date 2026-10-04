using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Audit;
using AgentCore.Application.Ports;
using AgentCore.Application.Tests.Diagnostics;
using AgentCore.Domain.Audit;
using Xunit;

namespace AgentCore.Application.Tests.Audit
{
    /// <summary>
    /// The one queue every audit sink sits behind.
    /// </summary>
    public sealed class QueuedAuditSinkTests
    {
        private const string ConversationId = "conversation-1";

        /// <summary>The id <c>Log.AuditQueueFull</c> carries.</summary>
        private const int AuditQueueFullEventId = 8;

        /// <summary>The id <c>Log.AuditAppendFailed</c> carries. It is unchanged by the queue.</summary>
        private const int AuditAppendFailedEventId = 5;

        [Fact]
        public async Task AnAppend_CompletesWhileTheInnerSinkIsStillWriting()
        {
            BlockingAuditSink inner = new();
            await using QueuedAuditSink sink = new(inner);

            ValueTask append = sink.AppendAsync(Event(1), TestContext.Current.CancellationToken);

            // The whole point. The inner sink holds every write open, and the caller is already done:
            // 91 nanoseconds to enqueue, and the 13 ms belongs to a thread the turn never sees.
            Assert.True(append.IsCompleted);
            await append;

            inner.Release();
        }

        [Fact]
        public async Task EveryEvent_ReachesTheInnerSink_InTheOrderItWasAppended()
        {
            InMemoryAuditSink inner = new();
            await using QueuedAuditSink sink = new(inner);

            List<Guid> ids = [];
            for (long sequence = 1; sequence <= 5; sequence++)
            {
                Guid id = Guid.CreateVersion7();
                ids.Add(id);
                await sink.AppendAsync(Event(sequence, id), TestContext.Current.CancellationToken);
            }

            await sink.FlushAsync(TestContext.Current.CancellationToken);

            // One reader drains the channel, so the chain reaches the store in the order the conversation
            // produced it. That ordering belongs to this class, not to each adapter.
            Assert.Equal(ids, inner.Events.Select(item => item.EventId).ToArray());
        }

        [Fact]
        public async Task EventsThatPileUpWhileTheSinkIsBusy_ArriveAsOneBatch()
        {
            GatedBatchAuditSink inner = new();
            await using QueuedAuditSink sink = new(inner);

            await sink.AppendAsync(Event(1), TestContext.Current.CancellationToken);

            // The writer is now inside the inner sink and reads nothing more, so what follows queues.
            await inner.Entered;

            for (long sequence = 2; sequence <= 21; sequence++)
            {
                await sink.AppendAsync(Event(sequence), TestContext.Current.CancellationToken);
            }

            inner.Release();
            await sink.FlushAsync(TestContext.Current.CancellationToken);

            // PostgreSQL wants rows in batches, and one round trip for twenty rows is the reason this
            // queue exists at all. The first batch is the one event that was there when the writer woke.
            Assert.Equal([1, 20], inner.BatchSizes);
        }

        [Fact]
        public async Task AFullQueueMakesTheCallerWaitAndDropsNothing()
        {
            RecordingLogger logger = new();
            GatedBatchAuditSink inner = new();
            await using QueuedAuditSink sink = new(inner, logger, capacity: 4);

            await sink.AppendAsync(Event(1), TestContext.Current.CancellationToken);
            await inner.Entered;

            List<Task> appends = [];
            for (long sequence = 2; sequence <= 8; sequence++)
            {
                appends.Add(sink.AppendAsync(Event(sequence), TestContext.Current.CancellationToken).AsTask());
            }

            Assert.False(appends[^1].IsCompleted);
            inner.Release();
            await Task.WhenAll(appends);

            await sink.FlushAsync(TestContext.Current.CancellationToken);
            Assert.Empty(logger.Of(AuditQueueFullEventId));
            Assert.Equal(8, inner.Written);
        }

        // The flush first waits for what is upstream of the queue (the audit hook's delivery).
        [Fact]
        public async Task AFlushWaitsForTheUpstreamBeforeTheQueue()
        {
            InMemoryAuditSink inner = new();
            TaskCompletionSource upstream = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await using QueuedAuditSink sink = new(inner) { Upstream = () => upstream.Task };

            Task flushing = sink.FlushAsync(TestContext.Current.CancellationToken).AsTask();
            Assert.False(flushing.IsCompleted);

            await sink.AppendAsync(Event(1), TestContext.Current.CancellationToken);
            upstream.SetResult();
            await flushing;

            _ = Assert.Single(inner.Events);
        }

        [Fact]
        public async Task AnInnerSinkThatRefuses_IsReportedAndTheQueueKeepsRunning()
        {
            RecordingLogger logger = new();
            ThrowingAuditSink inner = new();
            QueuedAuditSink sink = new(inner, logger);

            await sink.AppendAsync(Event(1), TestContext.Current.CancellationToken);
            await sink.DisposeAsync();

            // Audit is a record of the conversation and never a part of it. The store refused, the line says so,
            // and nothing was thrown at anyone.
            LogLine line = Assert.Single(logger.Of(AuditAppendFailedEventId));
            Assert.Equal(ThrowingAuditSink.Message, line.Exception?.Message);
        }

        [Fact]
        public async Task Disposal_WritesWhatIsStillQueued()
        {
            InMemoryAuditSink inner = new();
            QueuedAuditSink sink = new(inner);

            Guid id = Guid.CreateVersion7();
            await sink.AppendAsync(Event(1, id), TestContext.Current.CancellationToken);
            await sink.DisposeAsync();

            // A host that stops does not lose the rows it already accepted, which is what makes a queue
            // in front of the chain safe to put there.
            Assert.Equal(id, Assert.Single(inner.Events).EventId);
        }

        [Fact]
        public async Task AnAppendAfterDisposal_IsDroppedAndNeverThrows()
        {
            RecordingLogger logger = new();
            InMemoryAuditSink inner = new();
            QueuedAuditSink sink = new(inner, logger);

            await sink.DisposeAsync();
            ValueTask append = sink.AppendAsync(Event(1), TestContext.Current.CancellationToken);

            // Shutdown order is the host's, not this library's, so a late event is reported and dropped
            // rather than thrown back into whatever is still finishing.
            Assert.True(append.IsCompleted);
            await append;
            _ = Assert.Single(logger.Of(AuditQueueFullEventId));
            Assert.Empty(inner.Events);
        }

        private static AuditEvent Event(long sequence, Guid? eventId = null)
        {
            return new()
            {
                ConversationId = ConversationId,
                EventId = eventId ?? Guid.CreateVersion7(),
                Kind = AuditEventKind.TurnCompleted,
                OccurredAt = DateTimeOffset.UnixEpoch.AddSeconds(sequence),
            };
        }

        /// <summary>
        /// A batch-taking sink that holds its first batch open until a test releases it.
        /// </summary>
        private sealed class GatedBatchAuditSink : IAuditSinkPort
        {
            private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly Lock _gate = new();
            private readonly List<int> _batches = [];

            /// <summary>Completes once the sink is inside its first batch.</summary>
            public Task Entered => _entered.Task;

            /// <summary>Gets the size of each batch this sink took, oldest first.</summary>
            public IReadOnlyList<int> BatchSizes
            {
                get
                {
                    lock (_gate)
                    {
                        return [.. _batches];
                    }
                }
            }

            /// <summary>Gets how many events this sink took, across every batch.</summary>
            public int Written => BatchSizes.Sum();

            /// <summary>Lets the held batch complete, and every batch after it.</summary>
            public void Release()
            {
                _ = _release.TrySetResult();
            }

            public ValueTask AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
            {
                return AppendManyAsync([auditEvent], cancellationToken);
            }

            public async ValueTask AppendManyAsync(
                IReadOnlyList<AuditEvent> auditEvents,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(auditEvents);

                lock (_gate)
                {
                    _batches.Add(auditEvents.Count);
                }

                _ = _entered.TrySetResult();
                await _release.Task.ConfigureAwait(false);
            }
        }
    }
}
