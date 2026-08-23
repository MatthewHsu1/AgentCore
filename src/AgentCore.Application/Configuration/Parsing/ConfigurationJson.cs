using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.Configuration.Parsing;

/// <summary>
/// The reader settings that turn a checked document into the records of section 6.
/// </summary>
/// <remarks>
/// <para>
/// Check 1 of section 8.5 runs immediately before this and rejects every shape error, so the reader
/// is asked only to move values across, never to validate them. The converters below exist because
/// document values are written as strings, or as one of two interchangeable shapes, and modelled as
/// something richer: a tool result path, a secret template, a guard reference, and an MCP
/// <c>allow:</c> entry.
/// </para>
/// <para>
/// The settings are deliberately strict where a default would be lax. Property names match
/// case-sensitively, because <c>apiversion:</c> is not a key the schema accepts and silently binding
/// it would let a document pass check 1 by one spelling and bind by another. Numbers are not read
/// from strings, because <c>"0.5"</c> is a string in both YAML and JSON and the schema types the key
/// as a number. Unknown keys are skipped rather than refused, which matches the binder this
/// replaced: check 1's <c>additionalProperties: false</c> is what rejects them, and it has already
/// run.
/// </para>
/// </remarks>
internal static class ConfigurationJson
{
    /// <summary>Gets the settings the binder reads a document with.</summary>
    internal static JsonSerializerOptions Options { get; } = new()
    {
        // Every key in agentcore/v1 is camelCase, and every record property is the PascalCase of it.
        // The two exceptions carry a [JsonPropertyName]: state slot `enum` and telemetry
        // `exportIntervalMs`.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        Converters =
        {
            // Every enum in the document is written snake_case: `after_reply`, `group_chat`, and the
            // single-word ones such as `builtin`. Integers are refused, because no document may
            // write an enum as its ordinal.
            new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false),
            new ToolResultReferenceConverter(),
            new SecretTemplateConverter(),
            new GuardReferenceConverter(),
            new McpAllowEntryConverter(),
        },
    };
}

/// <summary>
/// Reads a <c>from:</c> value, as in <c>lookup_order.status</c>.
/// </summary>
/// <remarks>
/// Check 1 already holds the value to the pattern <c>^[^.\s]+\.[^\s]+$</c>, which is stricter than
/// <see cref="ToolResultReference.TryParse"/>. The failure branch is kept for a hand-built tree that
/// reached the binder without the check.
/// </remarks>
internal sealed class ToolResultReferenceConverter : JsonConverter<ToolResultReference>
{
    /// <inheritdoc />
    public override ToolResultReference Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("a string is expected");
        }

        var text = reader.GetString();
        return ToolResultReference.TryParse(text, out var reference)
            ? reference
            : throw new JsonException($"'{text}' is not a tool result path. The form is toolId.path.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ToolResultReference value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStringValue(value.ToString());
    }
}

/// <summary>
/// Reads a configuration string that may hold <c>${secret:name}</c> references.
/// </summary>
/// <remarks>
/// <see cref="SecretTemplate.Parse"/> resolves nothing and rejects nothing: text with no reference
/// in it is a template with no reference in it. So this converter has one failure of its own, a
/// value that is not a string at all.
/// </remarks>
internal sealed class SecretTemplateConverter : JsonConverter<SecretTemplate>
{
    /// <inheritdoc />
    public override SecretTemplate Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String
            ? SecretTemplate.Parse(reader.GetString()!)
            : throw new JsonException("a string is expected");

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, SecretTemplate value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStringValue(value.Raw);
    }
}

/// <summary>
/// Reads a <c>when:</c> value: a guard name, or an inline JSONLogic rule.
/// </summary>
/// <remarks>
/// Section 8.4 prefers the name. A string is read as a name and anything else as a rule, which is
/// the rule the document schema types as <c>#/$defs/rule</c>. An empty string is neither: it names
/// no guard, and check 2 would report it as an unknown guard called nothing at all, so it is refused
/// here with a message that says what happened.
/// </remarks>
internal sealed class GuardReferenceConverter : JsonConverter<GuardReference>
{
    /// <inheritdoc />
    public override GuardReference Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var name = reader.GetString();
            return string.IsNullOrEmpty(name)
                ? throw new JsonException("the guard name holds no text. Write the name of a guard, or an inline rule.")
                : GuardReference.FromName(name);
        }

        var rule = JsonSerializer.Deserialize<JsonNode>(ref reader, options)
                   ?? throw new JsonException("a guard name or an inline rule is expected, and the document holds null");

        return GuardReference.FromRule(rule);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, GuardReference value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        if (value.Name is { } name)
        {
            writer.WriteStringValue(name);
            return;
        }

        JsonSerializer.Serialize(writer, value.Rule, options);
    }
}
