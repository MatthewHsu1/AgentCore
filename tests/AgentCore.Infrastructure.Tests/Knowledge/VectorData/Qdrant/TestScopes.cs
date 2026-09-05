using AgentCore.Domain.Knowledge;

namespace AgentCore.Infrastructure.Tests.Knowledge.VectorData.Qdrant;

/// <summary>The scope a wildcard test opens, built from the facets it names.</summary>
internal static class TestScopes
{
    internal static KnowledgeScope Scope(params (string Key, string Value)[] facets) => new()
    {
        Facets = facets.ToDictionary(facet => facet.Key, facet => facet.Value, StringComparer.Ordinal),
    };
}
