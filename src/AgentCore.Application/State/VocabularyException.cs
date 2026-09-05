using System.Globalization;

namespace AgentCore.Application.State;

/// <summary>
/// The one exception <see cref="VocabularyCache.Replace"/> throws for each of section 10's four
/// degenerate reads.
/// </summary>
/// <remarks>
/// Boot fails a slot's read rather than starting an agent whose vocabulary can never work: a read
/// that hit its own limit cannot be told from a complete one, two ids that fold alike could never
/// be told apart by a caller who says either, and an id that folds to nothing could never be
/// linked or gated at all. Turning this into a configuration error with the slot's resolved
/// document pointer belongs to the startup wiring that knows that path, not to the cache.
/// </remarks>
public sealed class VocabularyException : Exception
{
    private const string DefaultMessage = "A slot's vocabulary could not be built.";

    /// <summary>Creates an exception that names no slot.</summary>
    public VocabularyException()
        : base(DefaultMessage) => Slot = string.Empty;

    /// <summary>Creates an exception with a plain message.</summary>
    /// <param name="message">The message a human reads.</param>
    public VocabularyException(string message)
        : base(message) => Slot = string.Empty;

    /// <summary>Creates an exception with a plain message and an inner cause.</summary>
    /// <param name="message">The message a human reads.</param>
    /// <param name="innerException">The cause.</param>
    public VocabularyException(string message, Exception? innerException)
        : base(message, innerException) => Slot = string.Empty;

    private VocabularyException(string slot, string message)
        : base(message) => Slot = slot;

    /// <summary>Gets the slot the failed vocabulary belongs to, or an empty string.</summary>
    public string Slot { get; }

    /// <summary>Builds the failure for a read with no candidate values once the wildcard sentinel is stripped.</summary>
    /// <param name="slot">The slot the vocabulary belongs to.</param>
    /// <returns>The failure.</returns>
    public static VocabularyException NoValues(string slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        return new VocabularyException(
            slot,
            $"the vocabulary for slot '{slot}' has no values once the wildcard sentinel is stripped. "
            + "A slot with no candidates can never be gated or linked.");
    }

    /// <summary>Builds the failure for a read whose count reached or passed the limit it was read with.</summary>
    /// <param name="slot">The slot the vocabulary belongs to.</param>
    /// <param name="maxValues">The limit the read was made with.</param>
    /// <returns>The failure.</returns>
    public static VocabularyException Truncated(string slot, int maxValues)
    {
        ArgumentNullException.ThrowIfNull(slot);
        return new VocabularyException(
            slot,
            $"the vocabulary for slot '{slot}' returned at least {maxValues.ToString(CultureInfo.InvariantCulture)} "
            + "values, its configured maxValues. That count cannot be told apart from a truncated read.");
    }

    /// <summary>Builds the failure for two values that normalise to the same string.</summary>
    /// <param name="slot">The slot the vocabulary belongs to.</param>
    /// <param name="first">The value already in the map.</param>
    /// <param name="second">The value that collided with it.</param>
    /// <param name="normalised">The normalised form both values share.</param>
    /// <returns>The failure.</returns>
    public static VocabularyException FoldingCollision(string slot, string first, string second, string normalised)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        ArgumentNullException.ThrowIfNull(normalised);
        return new VocabularyException(
            slot,
            $"the vocabulary for slot '{slot}' has two values that normalise alike: '{first}' and '{second}' "
            + $"both fold to '{normalised}'. A caller who says either could never be told apart.");
    }

    /// <summary>Builds the failure for a value that normalises to the empty string.</summary>
    /// <param name="slot">The slot the vocabulary belongs to.</param>
    /// <param name="value">The value that folded away.</param>
    /// <returns>The failure.</returns>
    public static VocabularyException FoldsToEmpty(string slot, string value)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(value);
        return new VocabularyException(
            slot,
            $"the vocabulary for slot '{slot}' has a value that normalises to the empty string: '{value}'. "
            + "It could never be linked or gated.");
    }
}
