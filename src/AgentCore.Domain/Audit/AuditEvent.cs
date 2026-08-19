namespace AgentCore.Domain.Audit;

/// <summary>
/// One append-only audit event. It is what a call wrote at one moment, and nothing edits it.
/// </summary>
public sealed record AuditEvent
{
    private static readonly IReadOnlyDictionary<string, string> EmptyPayload =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Gets the id of the call the event belongs to.</summary>
    public required string CallId { get; init; }

    /// <summary>Gets the position of the event inside its call, counting from zero.</summary>
    public required long Sequence { get; init; }

    /// <summary>Gets what the event records.</summary>
    public required AuditEventKind Kind { get; init; }

    /// <summary>Gets when the event happened.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    /// Gets the zero-based index of the turn the event belongs to, or <see langword="null"/> when the
    /// event belongs to the call rather than to one turn.
    /// </summary>
    public int? TurnIndex { get; init; }

    /// <summary>
    /// Gets the <see cref="Sequence"/> of the earlier event in the same call that this event corrects,
    /// or <see langword="null"/> when the event corrects nothing.
    /// </summary>
    public long? AmendsSequence { get; init; }

    /// <summary>Gets the facts the event carries, keyed by name.</summary>
    public IReadOnlyDictionary<string, string> Payload { get; init; } = EmptyPayload;
}
