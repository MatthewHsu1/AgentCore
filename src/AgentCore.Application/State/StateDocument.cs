using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.State;

/// <summary>
/// The declared state of one call, plus the three reserved slots.
/// </summary>
/// <remarks>
/// <para>
/// Section 8.3: every slot has exactly one writer, and guards read only declared state. One document
/// belongs to one call, so it takes no lock. The turn loop owns it and no other thread touches it.
/// </para>
/// <para>
/// A slot the writers have not filled is <em>unfilled</em>, and it reads as its declared default.
/// Unfilled and filled-false are therefore different states, which is what the nullable extractor
/// schema exists to preserve. <see cref="IsUnfilled(string)"/> reports the difference.
/// </para>
/// </remarks>
public sealed class StateDocument
{
    private readonly Dictionary<string, JsonNode?> _written = new(StringComparer.Ordinal);

    /// <summary>Creates the state of one call from the declared slots.</summary>
    /// <param name="configuration">The loaded document.</param>
    /// <param name="stage">The stage the call starts in, or <see langword="null"/> when there is no policy.</param>
    public StateDocument(AgentCoreConfiguration configuration, string? stage = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        Configuration = configuration;
        Stage = stage ?? configuration.Policy?.Initial ?? string.Empty;
    }

    /// <summary>Gets the document this state was declared by.</summary>
    public AgentCoreConfiguration Configuration { get; }

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
    /// when coercion failed, which leaves the slot at its default.
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

        _written[slot] = coerced;
        return true;
    }

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
