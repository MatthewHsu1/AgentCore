using System.Text.Json.Nodes;

namespace AgentCore.Application.Tools.Drawing;

/// <summary>Builds the one-line receipt the calling agent gets for a drawn tree.</summary>
internal static class DrawingReceipt
{
    /// <summary>
    /// Writes the one line the calling agent gets.
    /// </summary>
    /// <param name="tree">The tree that was drawn.</param>
    /// <returns>What was drawn, and every button with its payload.</returns>
    /// <remarks>
    /// The calling agent never sees the tree. A click arrives as caller words carrying the payload,
    /// so without the payloads named here it has no way to read one.
    /// </remarks>
    internal static string Describe(JsonObject tree)
    {
        List<string> actions = [];
        Collect(tree, actions);

        var root = tree["$type"]?.GetValue<string>() ?? "tree";

        return actions.Count == 0
            ? $"drew a {root}; buttons: none"
            : $"drew a {root}; buttons: {string.Join(", ", actions)}";
    }

    private static void Collect(JsonNode? node, List<string> actions)
    {
        switch (node)
        {
            case JsonArray list:
                foreach (var child in list)
                {
                    Collect(child, actions);
                }

                return;

            case JsonObject item:
                if (item["$action"] is JsonObject action
                    && action["type"]?.GetValue<string>() is { Length: > 0 } type)
                {
                    var payload = string.Join(
                        " ",
                        action
                            .Where(pair => !string.Equals(pair.Key, "type", StringComparison.Ordinal))
                            .Select(pair => $"{pair.Key}={pair.Value?.ToJsonString().Trim('"')}"));

                    actions.Add(payload.Length == 0 ? type : $"{type} {payload}");
                }

                foreach (var pair in item)
                {
                    if (!string.Equals(pair.Key, "$action", StringComparison.Ordinal))
                    {
                        Collect(pair.Value, actions);
                    }
                }

                return;
        }
    }
}
