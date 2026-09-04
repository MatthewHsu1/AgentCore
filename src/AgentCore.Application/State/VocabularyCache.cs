using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace AgentCore.Application.State;

/// <summary>
/// One immutable <see cref="VocabularyView"/> per <c>vocabulary:</c> slot, behind a reference a
/// refresh swaps rather than mutates.
/// </summary>
/// <remarks>
/// Section 5: a refresh builds a whole new view and replaces the reference, never mutating one in
/// place. That is what makes <see cref="Snapshot"/> free to call once at call open and safe to
/// hold for the rest of the call — the dictionary a caller already read never changes under it,
/// and a live refresh on another slot cannot make that reader tear or throw.
/// </remarks>
public sealed class VocabularyCache
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastGoodAt = new(StringComparer.Ordinal);
    private volatile ImmutableDictionary<string, VocabularyView> _views = ImmutableDictionary<string, VocabularyView>.Empty;

    /// <summary>Creates an empty cache.</summary>
    /// <param name="timeProvider">Where <see cref="LastGoodAt"/> comes from, or <see langword="null"/> for the system clock.</param>
    public VocabularyCache(TimeProvider? timeProvider = null) => _time = timeProvider ?? TimeProvider.System;

    /// <summary>Reads every slot's current vocabulary. The result does not change as later refreshes land.</summary>
    /// <returns>The map from slot name to its view. A slot <see cref="Replace"/> has never been called for is absent.</returns>
    public IReadOnlyDictionary<string, VocabularyView> Snapshot() => _views;

    /// <summary>Reads when a slot's vocabulary was last successfully built.</summary>
    /// <param name="slot">The slot name.</param>
    /// <returns>The time of the last successful <see cref="Replace"/>, or <see langword="null"/> when there has been none.</returns>
    public DateTimeOffset? LastGoodAt(string slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        return _lastGoodAt.TryGetValue(slot, out var at) ? at : null;
    }

    /// <summary>Builds one slot's vocabulary from a facet read and installs it, or leaves the slot as it was.</summary>
    /// <param name="slot">The slot the values were read for.</param>
    /// <param name="values">The distinct values the facet read returned, before the wildcard sentinel is stripped.</param>
    /// <param name="maxValues">The limit the read was made with — the same value passed as the read's own limit.</param>
    /// <param name="wildcardValue">
    /// The wildcard sentinel to strip before folding (K6), or <see langword="null"/> when this slot carries none.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxValues"/> is below 2, which is the same floor
    /// <c>ConfigurationValidator</c> holds the document to: a read of fewer than two values could
    /// never be told apart from a truncated one, so every call would throw.
    /// </exception>
    /// <exception cref="VocabularyException">
    /// <paramref name="values"/> fails one of section 10's four degenerate-read checks: its raw count
    /// reaches <paramref name="maxValues"/>; it is empty once <paramref name="wildcardValue"/> is
    /// stripped; two surviving values fold to the same string; or one folds to the empty string. The
    /// slot is left unchanged.
    /// </exception>
    public void Replace(string slot, IReadOnlyList<string> values, int maxValues, string? wildcardValue = null)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxValues, 2);

        // The raw count, before the sentinel is stripped: this answers "did the read hit its own
        // limit", which is a fact about what Qdrant returned, not about which of those values
        // happens to be the wildcard. K6 only orders the strip ahead of the collision checks.
        if (values.Count >= maxValues)
        {
            throw VocabularyException.Truncated(slot, maxValues);
        }

        var survivors = wildcardValue is null
            ? values
            : values.Where(value => !string.Equals(value, wildcardValue, StringComparison.Ordinal)).ToList();

        if (survivors.Count == 0)
        {
            throw VocabularyException.NoValues(slot);
        }

        Dictionary<string, string> normalisedToOriginal = new(survivors.Count, StringComparer.Ordinal);

        foreach (var original in survivors)
        {
            var normalised = VocabularyFold.Fold(original);

            if (normalised.Length == 0)
            {
                throw VocabularyException.FoldsToEmpty(slot, original);
            }

            if (normalisedToOriginal.TryGetValue(normalised, out var existing))
            {
                throw VocabularyException.FoldingCollision(slot, existing, original, normalised);
            }

            normalisedToOriginal[normalised] = original;
        }

        VocabularyView view = new()
        {
            NormalisedToOriginal = normalisedToOriginal,
            Originals = [.. survivors],
        };

        lock (_gate)
        {
            _views = _views.SetItem(slot, view);
            _lastGoodAt[slot] = _time.GetUtcNow();
        }
    }
}
