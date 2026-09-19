using AgentCore.Application.Ports;
using AgentCore.Domain.Audit;

namespace AgentCore.Application.Audit.Memory;

/// <summary>
/// The sink that keeps every event in a list.
/// </summary>
public sealed class InMemoryAuditSink : IAuditSinkPort
{
    private readonly Lock _gate = new();

    private readonly List<AuditEvent> _events = [];

    /// <summary>Gets the events this sink accepted, in the order they arrived.</summary>
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

    /// <summary>
    /// Reads back the events of one conversation, oldest first.
    /// </summary>
    /// <param name="conversationId">The id of the conversation.</param>
    /// <returns>The events of that conversation, in the order they arrived.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="conversationId"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<AuditEvent> EventsOf(string conversationId)
    {
        ArgumentNullException.ThrowIfNull(conversationId);

        lock (_gate)
        {
            return [.. _events.Where(item => string.Equals(item.ConversationId, conversationId, StringComparison.Ordinal))];
        }
    }

    /// <inheritdoc />
    public ValueTask AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        AuditEventVocabulary.Validate(auditEvent);

        lock (_gate)
        {
            _events.Add(auditEvent);
        }

        return ValueTask.CompletedTask;
    }
}
