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

    /// <summary>Gets what the event records.</summary>
    public required AuditEventKind Kind { get; init; }

    /// <summary>Gets when the event happened.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    /// Gets the zero-based index of the turn the event belongs to, or <see langword="null"/> when the
    /// event belongs to the call rather than to one turn.
    /// </summary>
    public int? TurnIndex { get; init; }

    /// <summary>Gets the identity of the event. The call allocates it, and it never orders anything.</summary>
    /// <remarks>
    /// It is a UUID v7, so it is unique without a round trip and an amendment can name the event it
    /// corrects the instant that event is raised. It is <em>not</em> an order: v7 is random within a
    /// millisecond, and a turn raises three events inside one. The order of a call is the
    /// <c>sequence</c> the store assigns, and nothing sorts by this value.
    /// </remarks>
    public required Guid EventId { get; init; }

    /// <summary>
    /// Gets the <see cref="EventId"/> of the earlier event in the same call that this event corrects,
    /// or <see langword="null"/> when the event corrects nothing.
    /// </summary>
    public Guid? AmendsEventId { get; init; }

    /// <summary>Gets the facts the event carries, keyed by name.</summary>
    public IReadOnlyDictionary<string, string> Payload { get; init; } = EmptyPayload;
}
