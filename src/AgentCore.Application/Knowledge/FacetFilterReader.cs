using System.Text.Json;

using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.State;

using Microsoft.Extensions.AI;

namespace AgentCore.Application.Knowledge;

/// <summary>
/// Reads the <c>filters</c> argument into the facets one search narrows by.
/// </summary>
internal static class FacetFilterReader
{
    /// <summary>Reads the argument, keeping only what the collection can match.</summary>
    /// <param name="arguments">What the model called the search tool with.</param>
    /// <param name="facets">What <c>scope.filterable</c> declared.</param>
    /// <param name="vocabulary">The cache a declared facet's values are linked through, or <see langword="null"/>.</param>
    /// <returns>The facet key to stored value. Empty when the argument named none, or none survived.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    internal static IReadOnlyDictionary<string, string> Read(
        AIFunctionArguments arguments,
        IReadOnlyList<KnowledgeFilterableFacetConfiguration> facets,
        VocabularyCache? vocabulary)
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

        var views = vocabulary?.Snapshot();

        foreach (var entry in entries.EnumerateArray())
        {
            if (Text(entry, FacetFilterSchema.KeyProperty) is not { Length: > 0 } key
                || Text(entry, FacetFilterSchema.ValueProperty) is not { Length: > 0 } value
                || !facets.Any(facet => string.Equals(facet.Key, key, StringComparison.Ordinal)))
            {
                continue;
            }

            if (Stored(views, key, value) is { } stored)
            {
                read[key] = stored;
            }
        }

        return read;
    }

    /// <summary>The stored value this mention names, or nothing when the collection holds none.</summary>
    private static string? Stored(
        IReadOnlyDictionary<string, VocabularyView>? views, string key, string value)
    {
        if (views is null || !views.TryGetValue(key, out var view))
        {
            return value;
        }

        return view.NormalisedToOriginal.TryGetValue(VocabularyFold.Fold(value), out var stored)
            ? stored
            : null;
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
