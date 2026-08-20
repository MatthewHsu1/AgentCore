using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.State;

/// <summary>
/// Writer 2 of section 8.3. It fills a slot from a named field of a named tool result.
/// </summary>
/// <remarks>
/// A tool result has no declared shape, so check 2 verifies only that the tool id exists and check 4
/// cannot type the slot from the path. The declared type of the slot wins: this writer coerces the
/// value, and leaves the slot at its default when coercion fails.
/// </remarks>
public static class ToolStateWriter
{
    /// <summary>Fills every slot that reads the result of one tool.</summary>
    /// <param name="state">The state of one call.</param>
    /// <param name="toolId">The id of the tool that just returned.</param>
    /// <param name="result">The tool result, as a node tree.</param>
    /// <returns>The number of slots this writer filled.</returns>
    public static int Apply(StateDocument state, string toolId, JsonNode? result)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrEmpty(toolId);

        var filled = 0;
        foreach (var (name, slot) in state.Configuration.State)
        {
            if (slot.Writer != StateWriter.Tool
                || slot.From is null
                || !string.Equals(slot.From.ToolId, toolId, StringComparison.Ordinal))
            {
                continue;
            }

            var value = ToolResultPath.Resolve(result, slot.From.Path);
            if (state.TryWrite(name, value))
            {
                filled++;
            }
        }

        return filled;
    }
}
