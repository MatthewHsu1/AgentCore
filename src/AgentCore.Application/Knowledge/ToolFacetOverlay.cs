using AgentCore.Domain.Knowledge;

namespace AgentCore.Application.Knowledge;

/// <summary>
/// Lays the facets a tool argument carried over the scope the turn composed.
/// </summary>
internal static class ToolFacetOverlay
{
    /// <summary>Writes the named facets into the scope, where the turn left room for them.</summary>
    /// <param name="scope">The scope the search would otherwise run under.</param>
    /// <param name="named">What the argument carried, or <see langword="null"/> when it carried nothing.</param>
    /// <returns>
    /// The scope to search under, and <see langword="true"/> when it differs from
    /// <paramref name="scope"/>. A caller that gets <see langword="true"/> holds both, so dropping
    /// these facets later is reopening the one it came in with rather than unpicking this one.
    /// </returns>
    internal static (KnowledgeScope Scope, bool Narrowed) Apply(
        KnowledgeScope scope, IReadOnlyDictionary<string, string>? named)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (named is not { Count: > 0 })
        {
            return (scope, false);
        }

        Dictionary<string, string> facets = new(scope.Facets, StringComparer.Ordinal);
        Dictionary<string, KnowledgeFacetOrigin> origins = new(scope.Origins, StringComparer.Ordinal);
        var wrote = false;

        foreach (var (key, value) in named)
        {
            if (scope.Origins.TryGetValue(key, out var origin)
                && origin is not KnowledgeFacetOrigin.Wildcard)
            {
                continue;
            }

            facets[key] = value;
            origins[key] = KnowledgeFacetOrigin.Tool;
            wrote = true;
        }

        return wrote
            ? (new KnowledgeScope { Facets = facets, Origins = origins }, true)
            : (scope, false);
    }
}
