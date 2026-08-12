namespace AgentCore.Domain.Audit;

/// <summary>
/// One append-only audit event. It is what a call wrote at one moment, and nothing edits it.
/// </summary>
/// <remarks>
/// <para>
/// D23 stacks three defences to keep this record append-only inside PostgreSQL. This type is the
/// first of them, in the sense that it carries no setter: a written event is a value, and a
/// correction is a second event that names the first through <see cref="AmendsSequence"/>.
/// </para>
/// <para>
/// The identity of an event is its hash and not <see cref="object.Equals(object?)"/>.
/// <see cref="Payload"/> is a dictionary, so two records that hold equal payloads still compare as
/// different references. Compare <see cref="AuditChain.ComputeHash"/> instead, which is what the
/// chain, the <c>CHECK</c> constraint, and every test compare.
/// </para>
/// </remarks>
public sealed record AuditEvent
{
    private static readonly IReadOnlyDictionary<string, string> EmptyPayload =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Gets the id of the call the event belongs to.</summary>
    public required string CallId { get; init; }

    /// <summary>Gets the position of the event inside its call, counting from zero.</summary>
    /// <remarks>
    /// <para>
    /// The caller allocates it, not the sink. The sink runs behind a bounded <c>Channel</c> and a
    /// background writer, so it answers long after the turn moved on, and a sequence the sink
    /// allocated would reach nobody in time.
    /// </para>
    /// <para>
    /// This is also why <see cref="AmendsSequence"/> names a sequence and not a hash. A hash
    /// reference is stronger, and it is unavailable: an insert costs 13 ms at p50, so the hash of
    /// the turn event does not exist when the barge-in amendment is enqueued 91 nanoseconds later.
    /// </para>
    /// </remarks>
    public required long Sequence { get; init; }

    /// <summary>Gets what the event records.</summary>
    public required AuditEventKind Kind { get; init; }

    /// <summary>Gets when the event happened.</summary>
    /// <remarks>
    /// The canonical form keeps the instant and drops the offset, so two hosts in two time zones hash
    /// one moment the same way. See <see cref="AuditChain.Canonicalize"/>.
    /// </remarks>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    /// Gets the zero-based index of the turn the event belongs to, or <see langword="null"/> when the
    /// event belongs to the call rather than to one turn.
    /// </summary>
    /// <remarks><see cref="AuditEventKind.CallStarted"/> and <see cref="AuditEventKind.CallEnded"/> carry no turn.</remarks>
    public int? TurnIndex { get; init; }

    /// <summary>
    /// Gets the <see cref="Sequence"/> of the earlier event in the same call that this event corrects,
    /// or <see langword="null"/> when the event corrects nothing.
    /// </summary>
    /// <remarks>
    /// T23: the chain is append-only, so an amendment is a second event that references the first.
    /// <see cref="AuditEventKind.ReplyInterrupted"/> must set it, and
    /// <see cref="AuditChain.Link"/> refuses the event when it is missing.
    /// </remarks>
    public long? AmendsSequence { get; init; }

    /// <summary>Gets the facts the event carries, keyed by name.</summary>
    /// <remarks>
    /// <see cref="AuditPayloadKeys"/> names the keys the design knows. The canonical form sorts the
    /// keys by ordinal, so the order a caller built the dictionary in never changes a hash.
    /// </remarks>
    public IReadOnlyDictionary<string, string> Payload { get; init; } = EmptyPayload;
}
