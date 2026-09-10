using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.Knowledge;

/// <summary>
/// Grows the framework's one-string search schema by a <c>filters</c> argument.
/// </summary>
internal static class FacetFilterSchema
{
    /// <summary>The argument the model fills to narrow its own search.</summary>
    internal const string ArgumentName = "filters";

    /// <summary>The key of one filter entry.</summary>
    internal const string KeyProperty = "key";

    /// <summary>The value of one filter entry.</summary>
    internal const string ValueProperty = "value";

    /// <summary>Adds the argument to a schema that does not carry it.</summary>
    /// <param name="innerSchema">The schema of the function being wrapped.</param>
    /// <param name="facets">What <c>scope.filterable</c> declared, in document order.</param>
    /// <returns>The extended schema.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="facets"/> is <see langword="null"/>.</exception>
    internal static JsonElement Extend(
        JsonElement innerSchema, IReadOnlyList<KnowledgeFilterableFacetConfiguration> facets)
    {
        ArgumentNullException.ThrowIfNull(facets);

        if (JsonSerializer.Deserialize<JsonNode>(innerSchema) is not JsonObject schema)
        {
            return innerSchema;
        }

        if (schema["properties"] is not JsonObject properties)
        {
            properties = [];
            schema["properties"] = properties;
        }

        properties[ArgumentName] = Argument(facets);

        return JsonSerializer.Deserialize<JsonElement>(schema);
    }

    private static JsonObject Argument(IReadOnlyList<KnowledgeFilterableFacetConfiguration> facets)
        => new()
        {
            ["type"] = "array",
            ["description"] = Wording(facets),
            ["items"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    [KeyProperty] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray([.. facets.Select(facet => (JsonNode)facet.Key)]),
                    },
                    [ValueProperty] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] =
                            "The value the cards must carry, as the collection stores it. Write what "
                            + "the person said and it will be matched against the stored values.",
                    },
                },
                ["required"] = new JsonArray(KeyProperty, ValueProperty),
            },
        };

    /// <summary>
    /// The one place a facet's meaning reaches the model.
    /// </summary>
    /// <remarks>
    /// Per key, because JSON Schema hangs no description off an enum member, and the alternative —
    /// an <c>anyOf</c> of <c>const</c> branches — is read unevenly across vendors.
    /// </remarks>
    private static string Wording(IReadOnlyList<KnowledgeFilterableFacetConfiguration> facets)
    {
        StringBuilder wording = new(
            "Narrow the search to cards carrying these facet values. Leave it out when the question "
            + "names none: a filter that matches nothing returns nothing. The keys are:");

        foreach (var facet in facets)
        {
            wording.Append("\n- ").Append(facet.Key).Append(": ").Append(facet.Description);
        }

        return wording.ToString();
    }
}
