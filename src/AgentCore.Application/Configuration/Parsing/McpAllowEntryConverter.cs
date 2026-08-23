using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.Configuration.Parsing;

/// <summary>
/// Reads one <c>allow:</c> entry: a tool name, or a single-key object aliasing it.
/// </summary>
/// <remarks>
/// Check 1 already holds an entry to one of the two shapes below, so the failure branches here exist
/// for a hand-built node tree that reached the binder without the check — a host passing
/// <c>options.Configuration</c> directly never went through check 1.
/// </remarks>
internal sealed class McpAllowEntryConverter : JsonConverter<McpAllowEntry>
{
    /// <inheritdoc />
    public override McpAllowEntry Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new McpAllowEntry { Name = reader.GetString()! };
        }

        var node = JsonSerializer.Deserialize<JsonNode>(ref reader, options);
        if (node is not JsonObject entry || entry.Count != 1)
        {
            throw new JsonException(
                $"an allow entry must be a string or a single-key object, and this is a {Describe(node)}");
        }

        var (name, aliasNode) = entry.Single();
        if (aliasNode is not JsonObject alias
            || alias.Count != 1
            || alias["as"] is not JsonValue asValue
            || asValue.GetValueKind() != JsonValueKind.String)
        {
            throw new JsonException($"'{name}' must map to an object holding only 'as', and this does not");
        }

        return new McpAllowEntry { Name = name, As = asValue.GetValue<string>() };
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, McpAllowEntry value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        if (value.As is null)
        {
            writer.WriteStringValue(value.Name);
            return;
        }

        writer.WriteStartObject();
        writer.WritePropertyName(value.Name);
        writer.WriteStartObject();
        writer.WriteString("as", value.As);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static string Describe(JsonNode? node)
        => node switch
        {
            null => "null",
            JsonObject obj => $"object with {obj.Count} properties",
            JsonArray => "array",
            JsonValue value => value.GetValueKind().ToString(),
            _ => node.GetType().Name,
        };
}
