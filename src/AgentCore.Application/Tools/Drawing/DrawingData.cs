using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Runtime;

namespace AgentCore.Application.Tools.Drawing;

/// <summary>
/// Tells the drawing model what its script's <c>data</c> holds: each tool's answer by name, its
/// shape, and a few rows, so the model can write the script without ever reading the rows.
/// </summary>
/// <remarks>
/// Three sample rows, strings cut at 60 characters, nesting cut at two levels, one line capped at
/// 600 characters. Measured on the same requests, a model given only this drew as faithfully as one
/// given every row, at half the input tokens; the rows do not help it and can only be retyped.
/// </remarks>
internal static class DrawingData
{
    private const int SampleRows = 3;

    private const int StringLimit = 60;

    private const int DepthLimit = 2;

    private const int LineLimit = 600;

    /// <summary>The samples are prose for a model, not a wire format: no escaping of the cut mark.</summary>
    private static readonly JsonSerializerOptions Prose = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Describes what a script will find in <c>data</c>.</summary>
    /// <param name="results">What this turn's tools answered, or <see langword="null"/>.</param>
    /// <returns>The block to append to the request, or <see langword="null"/> when nothing was answered.</returns>
    internal static string? Describe(TurnResults? results)
    {
        if (results is null || results.Tools.Count == 0)
        {
            return null;
        }

        StringBuilder text = new();
        text.AppendLine("## Data");
        text.AppendLine();
        text.AppendLine("Your script's `data` holds what this turn's tools answered. Read the rows from it; never retype them.");
        text.AppendLine();

        foreach (var tool in results.Tools)
        {
            var answers = results.Data()[TurnResults.AllKey]![tool]!.AsArray();
            var latest = answers[^1]!;
            var line = new StringBuilder();
            line.Append(CultureInfo.InvariantCulture, $"- `data.{tool}` — ").Append(Shape(latest));

            if (answers.Count > 1)
            {
                line.Append(CultureInfo.InvariantCulture, $" Called {answers.Count} times; `data.{TurnResults.AllKey}.{tool}` holds every answer, oldest first. Latest: ");
            }
            else
            {
                line.Append(latest is JsonArray ? $" First {SampleRows}: " : " It is: ");
            }

            line.Append(Sample(latest, depth: 0));
            text.AppendLine(Cap(line.ToString()));
        }

        return text.ToString().TrimEnd();
    }

    private static string Shape(JsonNode node)
        => node switch
        {
            JsonArray { Count: 0 } => "an empty array.",
            JsonArray list when list.All(item => item is JsonObject)
                => $"an array of {list.Count} objects with fields {Fields(list.OfType<JsonObject>().Take(SampleRows))}.",
            JsonArray list => $"an array of {list.Count} values.",
            JsonObject item => $"an object with fields {Fields([item])}.",
            _ => "a value.",
        };

    private static string Fields(IEnumerable<JsonObject> items)
    {
        List<string> names = [];
        foreach (var item in items)
        {
            foreach (var pair in item)
            {
                if (!names.Contains(pair.Key, StringComparer.Ordinal))
                {
                    names.Add(pair.Key);
                }
            }
        }

        return string.Join(", ", names);
    }

    private static string Sample(JsonNode? node, int depth)
    {
        switch (node)
        {
            case null:
                return "null";
            case JsonArray list:
                if (depth >= DepthLimit)
                {
                    return "[…]";
                }

                var items = list.Take(SampleRows).Select(item => Sample(item, depth + 1));
                var more = list.Count > SampleRows ? $", …+{list.Count - SampleRows}" : string.Empty;
                return $"[{string.Join(",", items)}{more}]";
            case JsonObject item:
                if (depth >= DepthLimit)
                {
                    return "{…}";
                }

                var pairs = item.Select(pair => $"\"{pair.Key}\":{Sample(pair.Value, depth + 1)}");
                return $"{{{string.Join(",", pairs)}}}";
            case JsonValue value when value.TryGetValue<string>(out var text):
                var shown = text.Length > StringLimit ? text[..StringLimit] + "…" : text;
                return JsonSerializer.Serialize(shown, Prose);
            default:
                return node.ToJsonString();
        }
    }

    private static string Cap(string line)
        => line.Length > LineLimit ? line[..LineLimit] + "…" : line;
}
