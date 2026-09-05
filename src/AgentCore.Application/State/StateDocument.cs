using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.State;

/// <summary>
/// The declared state of one call, plus the three reserved slots.
/// </summary>
/// <remarks>
/// <para>
/// Section 8.3: every slot has exactly one writer, and guards read only declared state. One document
/// belongs to one call, and the turn loop is its only WRITER, so nothing here serialises writes
/// against each other.
/// </para>
/// <para>
/// It is not, however, touched by one thread alone. <c>CallSession.Snapshot</c> reads the document
/// off the turn, and <c>AgentCoreAgent.SerializeSessionCoreAsync</c> is a framework seam any host
/// thread may call while a turn is running. That is why <see cref="_written"/> is concurrent: a
/// reader mid-<see cref="TryWrite"/> must get a torn answer and never an
/// <see cref="InvalidOperationException"/> out of the framework's own serialization API. A torn read
/// is acceptable where a throw is not, because the snapshot is best effort by design — D5 says the
/// next turn's own write corrects it, and the blob holds no counter that could collide.
/// </para>
/// <para>
/// A slot the writers have not filled is <em>unfilled</em>, and it reads as its declared default.
/// Unfilled and filled-false are therefore different states, which is what the nullable extractor
/// schema exists to preserve. <see cref="IsUnfilled(string)"/> reports the difference.
/// </para>
/// </remarks>
public sealed class StateDocument
{
    private static readonly IReadOnlyDictionary<string, VocabularyView> EmptyVocabulary =
        new Dictionary<string, VocabularyView>(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, JsonNode?> _written = new(StringComparer.Ordinal);

    private readonly IReadOnlyDictionary<string, VocabularyView> _vocabulary;

    /// <summary>Creates the state of one call from the declared slots.</summary>
    /// <param name="configuration">The loaded document.</param>
    /// <param name="stage">The stage the call starts in, or <see langword="null"/> when there is no policy.</param>
    /// <param name="vocabulary">
    /// Every <c>vocabulary:</c> slot's domain, sampled once at call open (K40), or
    /// <see langword="null"/> when the caller has none to give. A slot missing from this map
    /// refuses every write, the same as a slot missing from an <c>enum:</c>.
    /// </param>
    public StateDocument(
        AgentCoreConfiguration configuration,
        string? stage = null,
        IReadOnlyDictionary<string, VocabularyView>? vocabulary = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        Configuration = configuration;
        Stage = stage ?? configuration.Policy?.Initial ?? string.Empty;
        _vocabulary = vocabulary ?? EmptyVocabulary;
    }

    /// <summary>Gets the document this state was declared by.</summary>
    public AgentCoreConfiguration Configuration { get; }

    /// <summary>
    /// Gets every <c>vocabulary:</c> slot's domain, as it was sampled at call open (K40). The gate
    /// below and <see cref="StateExtractor"/>'s linker read this one map, so neither can hold a
    /// domain the other does not.
    /// </summary>
    internal IReadOnlyDictionary<string, VocabularyView> Vocabulary => _vocabulary;

    /// <summary>Gets or sets the reserved <c>stage</c> slot. The policy runtime owns it.</summary>
    public string Stage { get; set; }

    /// <summary>Gets or sets the reserved <c>turnIndex</c> slot. The turn loop owns it.</summary>
    public int TurnIndex { get; set; }

    /// <summary>Gets or sets the reserved <c>callDurationSeconds</c> slot. The turn loop owns it.</summary>
    public double CallDurationSeconds { get; set; }

    /// <summary>Gets the names of the declared slots. The reserved slots are not declared.</summary>
    public IEnumerable<string> SlotNames => Configuration.State.Keys;

    /// <summary>Reports whether no writer has filled a slot yet.</summary>
    /// <param name="slot">The slot name.</param>
    /// <returns><see langword="true"/> when the slot is declared and still unfilled.</returns>
    public bool IsUnfilled(string slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        return Configuration.State.ContainsKey(slot) && !_written.ContainsKey(slot);
    }

    /// <summary>Reads a slot. An unfilled slot reads as its declared default.</summary>
    /// <param name="slot">The slot name. A reserved name reads the reserved value.</param>
    /// <returns>The value, or <see langword="null"/> when the slot is unknown or has no default.</returns>
    public JsonNode? Read(string slot)
    {
        ArgumentNullException.ThrowIfNull(slot);

        if (ReservedStateSlots.Contains(slot))
        {
            return ReadReserved(slot);
        }

        if (_written.TryGetValue(slot, out var written))
        {
            return written;
        }

        return Configuration.State.TryGetValue(slot, out var declared)
            ? declared.Default?.DeepClone()
            : null;
    }

    /// <summary>Writes a slot after coercing the value to the declared type.</summary>
    /// <param name="slot">The declared slot name.</param>
    /// <param name="value">The raw value the writer produced.</param>
    /// <returns>
    /// <see langword="true"/> when the value coerced and the slot changed. <see langword="false"/>
    /// when coercion failed or the value is outside the slot's <c>enum</c>, which leaves the slot
    /// as it was.
    /// </returns>
    /// <exception cref="ArgumentException">The slot is reserved, and a reserved slot is read-only.</exception>
    public bool TryWrite(string slot, JsonNode? value)
    {
        ArgumentNullException.ThrowIfNull(slot);

        if (ReservedStateSlots.Contains(slot))
        {
            throw new ArgumentException($"The slot '{slot}' is reserved and read-only.", nameof(slot));
        }

        if (!Configuration.State.TryGetValue(slot, out var declared))
        {
            return false;
        }

        if (!StateValueCoercion.TryCoerce(value, declared.Type, out var coerced))
        {
            return false;
        }

        // A slot that names its members has a closed domain, and the knowledge scope reads such a
        // slot straight into a search filter. Refusing here is the only gate: the extractor's schema
        // is a request to the model, and Restore writes a host-supplied blob through this same path.
        if (declared.EnumValues is { Count: > 0 } members && !IsMember(members, coerced))
        {
            return false;
        }

        // K1/K8: vocabulary: is enum:'s large-domain sibling, read from a provider instead of
        // hand-written. The gate is the same shape — a slot missing from the snapshot (never
        // sampled, or a refresh that never landed) refuses every value, exactly as an enum: slot
        // with no members would.
        if (declared.Vocabulary is not null && !IsVocabularyMember(slot, coerced))
        {
            return false;
        }

        _written[slot] = coerced;
        return true;
    }

    // ToJsonString rather than JsonNode.DeepEquals, so the comparison works on every target
    // framework this package builds for.
    private static bool IsMember(IReadOnlyList<JsonNode> members, JsonNode? value)
    {
        var written = value?.ToJsonString();

        foreach (var member in members)
        {
            if (string.Equals(member.ToJsonString(), written, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // Through the fold map rather than a walk of Originals: a value is a member exactly when it
    // folds to a key the collection stores and that key maps back to this same spelling, which is
    // one dictionary probe instead of a scan of up to maxValues entries per write. It also reads
    // the one map the linker reads, so the gate and the linker cannot disagree about the domain.
    private bool IsVocabularyMember(string slot, JsonNode? value)
        => _vocabulary.TryGetValue(slot, out var view)
            && value is JsonValue text
            && text.TryGetValue<string>(out var written)
            && view.NormalisedToOriginal.TryGetValue(VocabularyFold.Fold(written), out var original)
            && string.Equals(original, written, StringComparison.Ordinal);

    /// <summary>Takes a snapshot the guards read. It holds every declared slot and the three reserved slots.</summary>
    /// <returns>The snapshot. It does not change when the document changes.</returns>
    public IReadOnlyDictionary<string, JsonNode?> Snapshot()
    {
        Dictionary<string, JsonNode?> snapshot = new(StringComparer.Ordinal);

        foreach (var slot in Configuration.State.Keys)
        {
            snapshot[slot] = Read(slot);
        }

        snapshot[ReservedStateSlots.Stage] = JsonValue.Create(Stage);
        snapshot[ReservedStateSlots.TurnIndex] = JsonValue.Create(TurnIndex);
        snapshot[ReservedStateSlots.CallDurationSeconds] = JsonValue.Create(CallDurationSeconds);
        return snapshot;
    }

    /// <summary>Reads the declared slots a writer has actually filled, for the durable blob.</summary>
    /// <returns>A copy. An unfilled slot is absent, which is what keeps unfilled and filled-default apart.</returns>
    /// <remarks>
    /// Not <see cref="Snapshot"/>: that one fills every declared slot with its default and adds the
    /// three reserved slots, which is right for a guard and wrong for a blob. Restoring a default as
    /// though a writer had chosen it would lose the difference <see cref="IsUnfilled(string)"/>
    /// exists to keep.
    /// </remarks>
    public IReadOnlyDictionary<string, JsonNode?> WrittenSlots()
    {
        Dictionary<string, JsonNode?> written = new(_written.Count, StringComparer.Ordinal);

        foreach (var entry in _written)
        {
            written[entry.Key] = entry.Value?.DeepClone();
        }

        return written;
    }

    private JsonValue? ReadReserved(string slot)
    {
        if (string.Equals(slot, ReservedStateSlots.Stage, StringComparison.Ordinal))
        {
            return JsonValue.Create(Stage);
        }

        return string.Equals(slot, ReservedStateSlots.TurnIndex, StringComparison.Ordinal)
            ? JsonValue.Create(TurnIndex)
            : JsonValue.Create(CallDurationSeconds);
    }
}
