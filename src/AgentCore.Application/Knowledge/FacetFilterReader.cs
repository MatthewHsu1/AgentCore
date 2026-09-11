using System.Text.Json;

using AgentCore.Application.Configuration.Schema;

using Microsoft.Extensions.AI;

namespace AgentCore.Application.Knowledge;

/// <summary>
/// Reads the <c>filters</c> argument into the facets one search narrows by.
/// </summary>
internal static class FacetFilterReader
{
    /// <summary>Reads the argument, keeping only the facets the document declared.</summary>
    /// <param name="arguments">What the model called the search tool with.</param>
    /// <param name="facets">What <c>scope.filterable</c> declared.</param>
    /// <returns>The facet key to value, as the model wrote it. Empty when the argument named none, or none survived.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    internal static IReadOnlyDictionary<string, string> Read(
        AIFunctionArguments arguments,
        IReadOnlyList<KnowledgeFilterableFacetConfiguration> facets)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(facets);

        Dictionary<string, string> read = new(StringComparer.Ordinal);

        if (!arguments.TryGetValue(FacetFilterSchema.ArgumentName, out var raw) || raw is null)
        {
            return read;
        }

        if (Array(raw) is not { } entries)
        {
            return read;
        }

        foreach (var entry in entries.EnumerateArray())
        {
            if (Text(entry, FacetFilterSchema.KeyProperty) is not { Length: > 0 } key
                || Text(entry, FacetFilterSchema.ValueProperty) is not { Length: > 0 } value
                || !facets.Any(facet => string.Equals(facet.Key, key, StringComparison.Ordinal)))
            {
                continue;
            }

            read[key] = value;
        }

        return read;
    }

    /// <summary>The argument as an array, whatever shape the caller handed it in.</summary>
    private static JsonElement? Array(object raw)
    {
        var element = raw as JsonElement? ?? Reserialized(raw);

        return element is { ValueKind: JsonValueKind.Array } array ? array : null;
    }

    private static JsonElement? Reserialized(object raw)
    {
        try
        {
            return JsonSerializer.SerializeToElement(raw);
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement entry, string property)
        => entry.ValueKind == JsonValueKind.Object
            && entry.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
}
