using System.Text.Json.Nodes;

namespace AgentCore.Application.Configuration.Schema;

/// <summary>
/// Value equality for the bound configuration records.
/// </summary>
/// <remarks>
/// <see cref="JsonNode"/> has reference equality, so a record that holds a raw JSONLogic rule
/// compares it through <see cref="JsonNode.DeepEquals"/> instead. Rule 17 of section 11 needs this:
/// the same document must load identically as YAML and as JSON.
/// </remarks>
internal static class ConfigurationEquality
{
    /// <summary>Compares two bound values.</summary>
    internal static bool ValueEquals<T>(T? left, T? right)
    {
        if (left is JsonNode leftNode)
        {
            return right is JsonNode rightNode && JsonNode.DeepEquals(leftNode, rightNode);
        }

        if (left is null)
        {
            return right is null;
        }

        return right is not null && EqualityComparer<T>.Default.Equals(left, right);
    }

    /// <summary>Hashes a bound value in a way that agrees with <see cref="ValueEquals{T}"/>.</summary>
    internal static int ValueHash<T>(T? value) => value switch
    {
        null => 0,

        // Two deep-equal nodes may hold different text, so every node hashes the same.
        JsonNode => 0x4A534F4E,
        _ => value.GetHashCode(),
    };
}
