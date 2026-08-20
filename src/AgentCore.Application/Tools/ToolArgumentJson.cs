using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tools;

/// <summary>
/// The one rule for carrying what the model filled into JSON.
/// </summary>
internal static class ToolArgumentJson
{
    /// <summary>Copies every argument into one JSON object.</summary>
    /// <param name="arguments">The arguments the model filled.</param>
    /// <returns>The object, with one property per argument.</returns>
    internal static JsonObject ToJsonObject(AIFunctionArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        JsonObject payload = [];
        foreach (var argument in arguments)
        {
            payload[argument.Key] = ToNode(argument.Value);
        }

        return payload;
    }

    /// <summary>Carries one tool argument into a JSON node.</summary>
    /// <param name="value">The value the model filled, in whatever type it arrived as.</param>
    /// <returns>The node, or <see langword="null"/> when the model filled nothing.</returns>
    internal static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node.DeepClone(),
        JsonElement element => JsonNode.Parse(element.GetRawText()),
        string text => JsonValue.Create(text),
        bool flag => JsonValue.Create(flag),
        int number => JsonValue.Create(number),
        long number => JsonValue.Create(number),
        double number => JsonValue.Create(number),
        decimal number => JsonValue.Create(number),
        _ => JsonValue.Create(value.ToString()),
    };
}
