using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AgentCore.Application.Configuration.Schema;

/// <summary>The declared type of one state slot.</summary>
public enum StateSlotType
{
    /// <summary><c>type: boolean</c>. Check 5 enumerates two points.</summary>
    Boolean,

    /// <summary><c>type: integer</c>.</summary>
    Integer,

    /// <summary><c>type: number</c>.</summary>
    Number,

    /// <summary><c>type: string</c>.</summary>
    String,
}

/// <summary>The one owner that fills a state slot. See the table in section 8.3.</summary>
public enum StateWriter
{
    /// <summary>A model call against a schema AgentCore builds from every extractor slot.</summary>
    Extractor,

    /// <summary>A named field of a named tool result.</summary>
    Tool,

    /// <summary>An integer that increments whenever its rule is true.</summary>
    Counter,

    /// <summary>A fixed value.</summary>
    Const,
}

/// <summary>
/// One declared state slot. Every slot has exactly one writer.
/// </summary>
public sealed record StateSlotConfiguration
{
    /// <summary>Gets the declared type. It is authoritative: AgentCore coerces the written value to it.</summary>
    public required StateSlotType Type { get; init; }

    /// <summary>Gets the one writer that owns the slot.</summary>
    public required StateWriter Writer { get; init; }

    /// <summary>Gets the value the slot holds before any writer fills it.</summary>
    public JsonNode? Default { get; init; }

    /// <summary>Gets the prose description of the slot, or <see langword="null"/>.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the members check 5 enumerates, or <see langword="null"/> when the slot is not an enumeration.</summary>
    /// <remarks>
    /// The document key is <c>enum</c>, which is a C# keyword, so the property carries the name it
    /// binds by rather than taking it from the naming policy.
    /// </remarks>
    [JsonPropertyName("enum")]
    public IReadOnlyList<JsonNode>? EnumValues { get; init; }

    /// <summary>Gets the tool result the slot reads. It is set when, and only when, the writer is <see cref="StateWriter.Tool"/>.</summary>
    public ToolResultReference? From { get; init; }

    /// <summary>Gets the raw JSONLogic rule that increments the slot. It is set when the writer is <see cref="StateWriter.Counter"/>.</summary>
    public JsonNode? Increment { get; init; }

    /// <summary>Gets the fixed value of the slot. It is set when the writer is <see cref="StateWriter.Const"/>.</summary>
    public JsonNode? Value { get; init; }
}
