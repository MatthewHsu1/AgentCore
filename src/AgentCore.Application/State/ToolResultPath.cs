using System.Text.Json.Nodes;
using Json.Pointer;

namespace AgentCore.Application.State;

/// <summary>
/// Reads one value out of an untyped tool result.
/// </summary>
/// <remarks>
/// Section 8.3: a <c>writer: tool</c> path is a JSON Pointer into a tool result, and a tool result
/// has no declared shape. The document writes the path in the compact <c>lookup_order.status</c>
/// form, so both the dotted form and the RFC 6901 pointer form resolve here. The dotted form is
/// normalised to a pointer - escaping <c>~</c> and <c>/</c> per RFC 6901 §3 - and then evaluated by
/// <see cref="JsonPointer"/>, so a segment that happens to contain either character still resolves
/// to the literal property name it named under the hand-rolled resolver this replaced.
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

        var pointerText = path.Length == 0 || path[0] == '/' ? path : ToPointer(path);

        if (!JsonPointer.TryParse(pointerText, out var pointer))
        {
            return null;
        }

        // RFC 6901 §4 defines the segment '-' as "the (nonexistent) member after the last array
        // element", and Json.Pointer.JsonPointer.TryEvaluate implements that by resolving it to the
        // *existing* last element. This repo does not adopt that reading: the hand-rolled resolver
        // this replaced gave '-' no meaning at all (an array index had to parse as a non-negative
        // integer, and '-' never does), so a path ending in '-' always reached nothing. That is kept
        // deliberately, not by oversight - a state slot must be written because a document's path
        // named a value, never as a side effect of which JSON Pointer library happens to evaluate
        // the path. So a '-' segment is rejected before it ever reaches the array it would otherwise
        // resolve against.
        for (var i = 0; i < pointer.SegmentCount; i++)
        {
            if (pointer[i].Equals("-"))
            {
                return null;
            }
        }

        return pointer.TryEvaluate(result, out var value) ? value : null;
    }

    /// <summary>Normalises the dotted compact form into an RFC 6901 pointer.</summary>
    /// <param name="path">A path with no leading slash, one segment for each dot-separated part.</param>
    /// <returns>The equivalent pointer, each segment escaped per RFC 6901 §3.</returns>
    private static string ToPointer(string path)
    {
        var segments = path.Split('.');
        for (var i = 0; i < segments.Length; i++)
        {
            // Escape '~' first, so a literal '~' in the dotted segment does not collide with the
            // '~0'/'~1' escape sequences the '/' replacement below introduces.
            segments[i] = segments[i]
                .Replace("~", "~0", StringComparison.Ordinal)
                .Replace("/", "~1", StringComparison.Ordinal);
        }

        return "/" + string.Join('/', segments);
    }
}
