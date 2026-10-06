using AgentCore.Application.Audit;
using AgentCore.Application.Tests.Diagnostics;
using AgentCore.Domain.Audit;
using AgentCore.TestSupport;
using Xunit;

namespace AgentCore.Application.Tests.Audit
{
    /// <summary>
    /// What the queue does with a batch the store refuses: a transient fault is retried, anything else is not.
    /// </summary>
    public sealed class QueuedAuditSinkRetryTests
    {
        /// <summary>The id <c>Log.AuditAppendFailed</c> carries.</summary>
        private const int AuditAppendFailedEventId = 5;

        private static readonly TimeSpan PastAnyRetryWait = TimeSpan.FromMinutes(1);

        private readonly FakeTimeProvider _time = new(DateTimeOffset.UnixEpoch);

        private readonly RecordingLogger _logger = new();

        [Fact]
        public async Task ABatchRefusedOnceWithATransientFault_IsWrittenOnce_AndBeforeEverythingAfterIt()
        {
            RefusingAuditSink inner = new((_, attempt) => attempt == 1 ? new FakeDbException(transient: true) : null);
            await using QueuedAuditSink sink = new(inner, _logger, timeProvider: _time);
            AuditEvent first = Event();
            AuditEvent second = Event();

            await sink.AppendAsync(first, TestContext.Current.CancellationToken);
            await inner.NextAttemptAsync();
            await RetryWaitArmedAsync();

            // Queued while the refused batch waits for its retry, so it may only land after it.
            await sink.AppendAsync(second, TestContext.Current.CancellationToken);

            // The wait is on the injected clock: until it moves, nothing is tried again.
            _ = Assert.Single(inner.Attempts);

            _time.Advance(PastAnyRetryWait);
            await sink.FlushAsync(TestContext.Current.CancellationToken);

            Assert.Equal([first.EventId, second.EventId], inner.Events);
            Assert.Empty(_logger.Of(AuditAppendFailedEventId));
        }

        [Fact]
        public async Task ABatchRefusedEveryTime_IsDroppedAfterThreeRetries_WithOneError_AndLaterBatchesStillLand()
        {
            AuditEvent doomed = Event();
            AuditEvent later = Event();
            RefusingAuditSink inner = new((batch, _) =>
                batch.Any(item => item.EventId == doomed.EventId) ? new FakeDbException(transient: true) : null);
            await using QueuedAuditSink sink = new(inner, _logger, timeProvider: _time);

            await sink.AppendAsync(doomed, TestContext.Current.CancellationToken);
            await inner.NextAttemptAsync();

            for (int retry = 1; retry <= 3; retry++)
            {
                await RetryWaitArmedAsync();
                _time.Advance(PastAnyRetryWait);
                await inner.NextAttemptAsync();
            }

            await sink.AppendAsync(later, TestContext.Current.CancellationToken);
            await sink.FlushAsync(TestContext.Current.CancellationToken);

            Assert.Equal(4, inner.Attempts.Count(batch => batch.Contains(doomed.EventId)));
            LogLine line = Assert.Single(_logger.Of(AuditAppendFailedEventId));
            _ = Assert.IsType<FakeDbException>(line.Exception);
            Assert.Equal([later.EventId], inner.Events);
        }

        [Theory]
        [InlineData("malformed")]
        [InlineData("permanent-db")]
        [InlineData("other")]
        public async Task ABatchRefusedPermanently_IsDroppedAtOnce_WithoutARetry(string refusal)
        {
            Exception cause = refusal switch
            {
                "malformed" => new ArgumentException("The ending names no reason."),
                "permanent-db" => new FakeDbException(transient: false),
                _ => new InvalidOperationException("the audit store is unreachable."),
            };
            RefusingAuditSink inner = new((_, _) => cause);
            await using QueuedAuditSink sink = new(inner, _logger, timeProvider: _time);

            await sink.AppendAsync(Event(), TestContext.Current.CancellationToken);

            // Completes with the clock never moved, because there is no wait to move it past.
            await sink.FlushAsync(TestContext.Current.CancellationToken)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            _ = Assert.Single(inner.Attempts);
            Assert.Same(cause, Assert.Single(_logger.Of(AuditAppendFailedEventId)).Exception);
        }

        [Fact]
        public async Task DisposalDuringARetryWait_EndsTheWaitWithinTheWindow_AndDropsTheBatchOnce()
        {
            RefusingAuditSink inner = new((_, _) => new FakeDbException(transient: true));
            QueuedAuditSink sink = new(inner, _logger, timeProvider: _time);

            await sink.AppendAsync(Event(), TestContext.Current.CancellationToken);
            await inner.NextAttemptAsync();
            await RetryWaitArmedAsync();

            // The clock never moves, so the wait only ends because disposal cancels it after its window.
            await sink.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            _time.Advance(PastAnyRetryWait);

            _ = Assert.Single(inner.Attempts);
            LogLine line = Assert.Single(_logger.Of(AuditAppendFailedEventId));
            _ = Assert.IsType<OperationCanceledException>(line.Exception, exactMatch: false);
            Assert.Empty(inner.Events);
        }

        /// <summary>Waits until the queue has armed its wait before the next attempt.</summary>
        private Task RetryWaitArmedAsync()
        {
            return _time.WaitForTimersAsync(1).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }

        private static AuditEvent Event()
        {
            return new()
            {
                ConversationId = "conversation-1",
                EventId = Guid.CreateVersion7(),
                Kind = AuditEventKind.TurnCompleted,
                OccurredAt = DateTimeOffset.UnixEpoch,
            };
        }
    }
}
