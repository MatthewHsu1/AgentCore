namespace AgentCore.Domain.Knowledge;

/// <summary>Where one facet's value came from.</summary>
public enum KnowledgeFacetOrigin
{
    /// <summary>The host resolved it and opened it on the turn.</summary>
    Host,

    /// <summary>The extractor read it out of what the caller said.</summary>
    Extractor,

    /// <summary>Nothing knew it, so the facet holds the wildcard and narrows nothing.</summary>
    Wildcard,
}

/// <summary>
/// What one turn is allowed to see of the knowledge base.
/// </summary>
public sealed record KnowledgeScope
{
    private static readonly IReadOnlyDictionary<string, KnowledgeFacetOrigin> NoOrigins =
        new Dictionary<string, KnowledgeFacetOrigin>(StringComparer.Ordinal);

    /// <summary>Gets the facet key to required value. Every entry is ANDed into the search filter.</summary>
    public required IReadOnlyDictionary<string, string> Facets { get; init; }

    /// <summary>
    /// Gets where each facet's value came from, or empty when nothing recorded it.
    /// </summary>
    public IReadOnlyDictionary<string, KnowledgeFacetOrigin> Origins { get; init; } = NoOrigins;
}
