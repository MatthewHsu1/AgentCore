using System.Collections.ObjectModel;

namespace AgentCore.Application.Runtime;

/// <summary>
/// One fact about a call, as the turn loop saw it. Nothing edits it afterwards.
/// </summary>
/// <remarks>
/// <para>
/// This is the currency of the observer hook. The turn loop knows what happened and nothing about
/// what anyone does with it, so an audit row, a metric, and a log line are three readings of the same
/// fact rather than three call sites inside <see cref="CallSession"/>.
/// </para>
/// <para>
/// It is neutral on purpose. There is no sequence, no hash, and no wire token here, because those
/// belong to the chain of D23 and not to the call: a session that named them would know what an audit
/// sink is, which is the coupling this type removes. <c>AuditCallObserver</c> turns
/// <see cref="Ordinal"/> into <see cref="Domain.Audit.AuditEvent.Sequence"/> and
/// <see cref="Kind"/> into a wire token, and every other observer ignores both.
/// </para>
/// <para>
/// The identity of an event is the fact it carries and not <see cref="object.Equals(object?)"/>.
/// <see cref="Payload"/> is a dictionary, so two records that hold equal payloads still compare as
/// different references.
/// </para>
/// </remarks>
public sealed record CallEvent
{
    /// <summary>Gets the id of the call the fact belongs to.</summary>
    public required string CallId { get; init; }

    /// <summary>Gets what happened.</summary>
    public required CallEventKind Kind { get; init; }

    /// <summary>Gets the moment it happened, read from the clock of the session.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    /// Gets the position of this fact among the durable facts of its call, counting from zero, or
    /// <see langword="null"/> for a diagnostic-only event, which no audit row records.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The session allocates it, not the sink, for the reason
    /// <see cref="Domain.Audit.AuditEvent.Sequence"/> gives: the sink answers long after the turn
    /// moved on, and a number the sink allocated would reach nobody in time.
    /// </para>
    /// <para>
    /// It is nullable because a diagnostic event must not consume a number.
    /// <see cref="CallEventKind.EmptyReply"/> and the three other diagnostic kinds are counted and
    /// logged and stored nowhere, so they leave this null and the chain stays gap-free and monotonic
    /// from zero, exactly as it was before the hook existed.
    /// </para>
    /// </remarks>
    public long? Ordinal { get; init; }

    /// <summary>
    /// Gets the zero-based index of the turn the fact belongs to, or <see langword="null"/> for a
    /// fact about the call itself.
    /// </summary>
    /// <remarks>
    /// <see cref="CallEventKind.CallStarted"/> and <see cref="CallEventKind.CallEnded"/> are the two
    /// kinds that carry no turn: neither of them happens inside one.
    /// </remarks>
    public int? TurnIndex { get; init; }

    /// <summary>
    /// Gets the <see cref="Ordinal"/> of the earlier fact this one corrects, or
    /// <see langword="null"/> when it corrects nothing.
    /// </summary>
    /// <remarks>
    /// The chain is append-only, so nothing here rewrites an earlier event in place. A correction is
    /// a second event that names the first. Only <see cref="CallEventKind.ReplyInterrupted"/> sets
    /// this, and it names the <see cref="CallEventKind.TurnCompleted"/> of the same turn, per T23.
    /// </remarks>
    public long? AmendsOrdinal { get; init; }

    /// <summary>Gets the detail of the fact, keyed by name. It is empty when the kind needs none.</summary>
    /// <remarks>
    /// <see cref="Domain.Audit.AuditPayloadKeys"/> names the keys the design knows, and an observer
    /// that reads one reads it from there rather than from a literal.
    /// </remarks>
    public IReadOnlyDictionary<string, string> Payload { get; init; } = ReadOnlyDictionary<string, string>.Empty;
}
