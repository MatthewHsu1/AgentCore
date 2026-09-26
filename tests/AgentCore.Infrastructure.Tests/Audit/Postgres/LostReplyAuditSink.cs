using AgentCore.Application.Ports;
using AgentCore.Domain.Audit;
using Npgsql;

namespace AgentCore.Infrastructure.Tests.Audit.Postgres
{
    /// <summary>
    /// A store whose first batch commits and then loses its reply, the way a connection that drops after
    /// COMMIT does.
    /// </summary>
    internal sealed class LostReplyAuditSink(IAuditSinkPort inner) : IAuditSinkPort
    {
        private readonly TaskCompletionSource _lostReply = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once the first batch has committed and its reply was lost.</summary>
        public Task LostReply => _lostReply.Task;

        public ValueTask AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            return AppendManyAsync([auditEvent], cancellationToken);
        }

        public async ValueTask AppendManyAsync(
            IReadOnlyList<AuditEvent> auditEvents,
            CancellationToken cancellationToken = default)
        {
            await inner.AppendManyAsync(auditEvents, cancellationToken);

            if (_lostReply.TrySetResult())
            {
                throw new NpgsqlException("The reply to COMMIT was lost.", new IOException("The connection dropped."));
            }
        }
    }
}
