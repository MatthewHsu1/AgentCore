namespace AgentCore.Application.State;

/// <summary>
/// One slot's vocabulary: every original value the collection stores, and the map from each
/// value's normalised form back to that original.
/// </summary>
/// <remarks>
/// Immutable, so a snapshot taken once at call open never changes under the call that holds it —
/// section 5's requirement that a refresh landing mid-call cannot make the gate and the linker
/// disagree, because both read the one reference <see cref="VocabularyCache.Snapshot"/> handed out.
/// </remarks>
public sealed record VocabularyView
{
    /// <summary>Gets the map from a value's normalised form (the <c>VocabularyFold.Fold</c> result) back to the original string.</summary>
    public required IReadOnlyDictionary<string, string> NormalisedToOriginal { get; init; }

    /// <summary>Gets every original value, in the order the read returned them, with the wildcard sentinel already stripped.</summary>
    public required IReadOnlyList<string> Originals { get; init; }
}
