using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tools;

/// <summary>
/// The one rule for reading what a tool answered.
/// </summary>
/// <remarks>
/// A tool result has no declared shape. It arrives as a node, as an element, as text that may or
/// may not be JSON, or as an object of the tool's own. Everything that reads a result reads it
/// through here, so no two readers disagree about what one answer means.
/// </remarks>
public static class ToolResultJson
{
    /// <summary>Carries one tool result into a JSON node.</summary>
    /// <param name="value">Whatever the tool returned.</param>
    /// <returns>The node tree, or <see langword="null"/> when the tool returned nothing.</returns>
    public static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node.DeepClone(),

        // A tool result has no declared shape. A tool that answers with a JSON document as one
        // string still reaches its slot, and a tool that answers with prose reads as that prose.
        JsonElement element when element.ValueKind is JsonValueKind.String
            => ParseOrText(element.GetString() ?? string.Empty),
        JsonElement element => JsonNode.Parse(element.GetRawText()),
        string text => ParseOrText(text),
        bool flag => JsonValue.Create(flag),
        int number => JsonValue.Create(number),
        long number => JsonValue.Create(number),
        double number => JsonValue.Create(number),
        decimal number => JsonValue.Create(number),

        // A tool may answer with an object of its own. Its type name is not its answer, so it is
        // serialised the same way the model was shown it.
        _ => JsonSerializer.SerializeToNode(value, value.GetType(), AIJsonUtilities.DefaultOptions),
    };

    /// <summary>Reads one string as JSON, and falls back to the string itself.</summary>
    /// <param name="text">The text the tool returned.</param>
    /// <returns>The node tree.</returns>
    private static JsonNode? ParseOrText(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            // Section 8.7: a tool result has no declared shape, and a tool never drops a turn.
            return JsonValue.Create(text);
        }
    }
}
