using System.Collections.ObjectModel;

namespace AgentCore.Application.Runtime;

/// <summary>
/// One fact about a call, as the turn loop saw it. Nothing edits it afterwards.
/// </summary>
public sealed record CallEvent
{
    /// <summary>Gets the id of the call the fact belongs to.</summary>
    public required string CallId { get; init; }

    /// <summary>Gets what happened.</summary>
    public required CallEventKind Kind { get; init; }

    /// <summary>Gets the moment it happened, read from the clock of the session.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    /// Gets the identity of this fact, or <see langword="null"/> for a diagnostic-only event, which
    /// no audit row records.
    /// </summary>
    public Guid? EventId { get; init; }

    /// <summary>
    /// Gets the zero-based index of the turn the fact belongs to, or <see langword="null"/> for a
    /// fact about the call itself.
    /// </summary>
    public int? TurnIndex { get; init; }

    /// <summary>
    /// Gets the <see cref="EventId"/> of the earlier fact this one corrects, or
    /// <see langword="null"/> when it corrects nothing.
    /// </summary>
    public Guid? AmendsEventId { get; init; }

    /// <summary>Gets the detail of the fact, keyed by name. It is empty when the kind needs none.</summary>
    public IReadOnlyDictionary<string, string> Payload { get; init; } = ReadOnlyDictionary<string, string>.Empty;
}
