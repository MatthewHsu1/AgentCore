using System.Threading.Channels;
using AgentCore.Application.Ports;
using AgentCore.Domain.Audit;
using Xunit;

namespace AgentCore.Application.Tests.Audit
{
    /// <summary>
    /// A batch-taking sink that refuses whichever attempts a test tells it to, and records every attempt.
    /// </summary>
    /// <param name="refuse">
    /// Given the batch and the one-based number of the attempt across the sink's life, returns the exception
    /// to refuse it with, or <see langword="null"/> to accept it.
    /// </param>
    internal sealed class RefusingAuditSink(Func<IReadOnlyList<AuditEvent>, int, Exception?> refuse) : IAuditSinkPort
    {
        private readonly Lock _gate = new();
        private readonly List<Guid[]> _attempts = [];
        private readonly List<AuditEvent> _events = [];
        private readonly Channel<int> _attempted = Channel.CreateUnbounded<int>();

        /// <summary>Gets the event ids of every batch the queue handed over, accepted or not, oldest first.</summary>
        public IReadOnlyList<Guid[]> Attempts
        {
            get
            {
                lock (_gate)
                {
                    return [.. _attempts];
                }
            }
        }

        /// <summary>Gets the ids of the events this sink accepted, in the order they arrived.</summary>
        public IReadOnlyList<Guid> Events
        {
            get
            {
                lock (_gate)
                {
                    return [.. _events.Select(item => item.EventId)];
                }
            }
        }

        /// <summary>Waits for the next attempt, accepted or refused, to have been made.</summary>
        /// <returns>A task that completes once one more attempt was made, or times out after ten seconds.</returns>
        public Task NextAttemptAsync()
        {
            return _attempted.Reader
                .ReadAsync(TestContext.Current.CancellationToken)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }

        public ValueTask AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            return AppendManyAsync([auditEvent], cancellationToken);
        }

        public ValueTask AppendManyAsync(IReadOnlyList<AuditEvent> auditEvents, CancellationToken cancellationToken = default)
        {
            Exception? refusal;
            int attempt;
            lock (_gate)
            {
                _attempts.Add([.. auditEvents.Select(item => item.EventId)]);
                attempt = _attempts.Count;
                refusal = refuse(auditEvents, attempt);
                if (refusal is null)
                {
                    _events.AddRange(auditEvents);
                }
            }

            _ = _attempted.Writer.TryWrite(attempt);
            return refusal is null ? ValueTask.CompletedTask : ValueTask.FromException(refusal);
        }
    }
}
