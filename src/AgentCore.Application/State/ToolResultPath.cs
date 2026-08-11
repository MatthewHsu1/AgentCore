using System.Globalization;
using System.Text.Json.Nodes;

namespace AgentCore.Application.State;

/// <summary>
/// Reads one value out of an untyped tool result.
/// </summary>
/// <remarks>
/// Section 8.3: a <c>writer: tool</c> path is a JSON Pointer into a tool result, and a tool result
/// has no declared shape. The document writes the path in the compact <c>lookup_order.status</c>
/// form, so both the dotted form and the RFC 6901 pointer form resolve here.
/// </remarks>
internal static class ToolResultPath
{
    /// <summary>Resolves a path against a tool result.</summary>
    /// <param name="result">The tool result, as a node tree.</param>
    /// <param name="path">The dotted path, or the RFC 6901 pointer.</param>
    /// <returns>The node the path names, or <see langword="null"/> when the path reaches nothing.</returns>
    public static JsonNode? Resolve(JsonNode? result, string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (result is null)
        {
            return null;
        }

        var current = result;
        foreach (var segment in Split(path))
        {
            current = Step(current, segment);
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    private static IEnumerable<string> Split(string path)
    {
        if (path.Length == 0)
        {
            yield break;
        }

        if (path[0] == '/')
        {
            foreach (var raw in path[1..].Split('/'))
            {
                yield return raw
                    .Replace("~1", "/", StringComparison.Ordinal)
                    .Replace("~0", "~", StringComparison.Ordinal);
            }

            yield break;
        }

        foreach (var raw in path.Split('.'))
        {
            yield return raw;
        }
    }

    private static JsonNode? Step(JsonNode current, string segment) => current switch
    {
        JsonObject map => map.TryGetPropertyValue(segment, out var value) ? value : null,
        JsonArray list when int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
            && index >= 0 && index < list.Count => list[index],
        _ => null,
    };
}
