using System.Diagnostics.CodeAnalysis;

namespace AgentCore.Application.Configuration.Schema;

/// <summary>
/// A <c>writer: tool</c> path, as in <c>lookup_order.status</c>.
/// </summary>
/// <remarks>
/// A tool result has no declared shape, so check 2 verifies only that <see cref="ToolId"/> exists,
/// and check 4 cannot type the slot from <see cref="Path"/>. See section 8.3.
/// </remarks>
/// <param name="ToolId">The id of the tool whose result the slot reads.</param>
/// <param name="Path">The path into that result. The text after the first dot.</param>
public sealed record ToolResultReference(string ToolId, string Path)
{
    /// <summary>Reads a <c>from:</c> value.</summary>
    /// <param name="text">The raw value, such as <c>lookup_order.status</c>.</param>
    /// <param name="reference">The parsed reference, when the text holds a dot.</param>
    /// <returns><see langword="true"/> when the text parses.</returns>
    public static bool TryParse(string? text, [NotNullWhen(true)] out ToolResultReference? reference)
    {
        reference = null;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var dot = text.IndexOf('.');
        if (dot <= 0 || dot == text.Length - 1)
        {
            return false;
        }

        reference = new ToolResultReference(text[..dot], text[(dot + 1)..]);
        return true;
    }

    /// <summary>Writes the reference back in its source form.</summary>
    /// <returns>The text <c>toolId.path</c>.</returns>
    public override string ToString() => ToolId + "." + Path;
}
