namespace AgentCore.Domain.Knowledge;

/// <summary>
/// What one turn is allowed to see of the knowledge base.
/// </summary>
public sealed record KnowledgeScope
{
    /// <summary>Gets the facet key to required value. Every entry is ANDed into the search filter.</summary>
    public required IReadOnlyDictionary<string, string> Facets { get; init; }
}
