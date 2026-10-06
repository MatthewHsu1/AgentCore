using AgentCore.Application.Ports;
using AgentCore.Domain.Audit;

namespace AgentCore.AspNetCore.Tests.Fakes
{
    /// <summary>An audit store that keeps what it accepted, so what the document opened is observable.</summary>
    internal sealed class RecordingAuditSink : IAuditSinkPort
    {
        private readonly Lock _gate = new();
        private readonly List<AuditEvent> _events = [];

        /// <summary>Gets the events this store accepted, in the order they arrived.</summary>
        public IReadOnlyList<AuditEvent> Events
        {
            get
            {
                lock (_gate)
                {
                    return [.. _events];
                }
            }
        }

        public ValueTask AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _events.Add(auditEvent);
            }

            return ValueTask.CompletedTask;
        }
    }
}
