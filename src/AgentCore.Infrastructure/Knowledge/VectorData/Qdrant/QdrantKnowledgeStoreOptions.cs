using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Knowledge;

namespace AgentCore.Infrastructure.Knowledge.VectorData.Qdrant;

/// <summary>What one bound store reads. The document sets these once, at startup.</summary>
internal sealed record QdrantKnowledgeStoreOptions
{
    /// <summary>Gets the collection to read. Always the alias.</summary>
    public required string Collection { get; init; }

    /// <summary>Gets whether this deployment confines searches to a scope.</summary>
    public required bool Scoped { get; init; }

    /// <summary>Gets the vector name every query searches, or <see langword="null"/> for the single anonymous vector.</summary>
    public string? VectorName { get; init; }

    /// <summary>Gets how a card's payload is named.</summary>
    public KnowledgeFieldsConfiguration Fields { get; init; } = new();

    /// <summary>Gets what turns one point into a card, or <see langword="null"/> for the <c>fields:</c> mapping.</summary>
    public IKnowledgePointMapper? Mapper { get; init; }

    /// <summary>Gets the payload path each facet key becomes, with <c>{key}</c> for the key.</summary>
    public string ScopeTemplate { get; init; } = KnowledgeScopeConfiguration.DefaultTemplate;

    /// <summary>Gets how links between cards are read and followed, or <see langword="null"/> for no expansion.</summary>
    public KnowledgeLinksConfiguration? Links { get; init; }

    /// <summary>Gets the uuid5 namespace, already resolved from <see cref="KnowledgeLinksConfiguration.Namespace"/>.</summary>
    public Guid LinkNamespace { get; init; } = KbPointId.Namespace(KnowledgeLinksConfiguration.DefaultNamespace);

    /// <summary>Gets what picks the terms a result must contain.</summary>
    public IKnowledgeQueryAnalyzer Analyzer { get; init; } = new IdentifierCodeAnalyzer();

    /// <summary>Gets how many fused results to return. This is the deployment's ceiling, not an agent's.</summary>
    public int Limit { get; init; } = 5;

    /// <summary>
    /// Gets the smallest fused score a card may carry, in the range 0 to 1.
    /// </summary>
    public double ScoreFloor { get; init; } = KnowledgeProviderConfiguration.DefaultScoreFloor;

    /// <summary>Gets how long a search may take before it is abandoned.</summary>
    public TimeSpan Deadline { get; init; } = TimeSpan.FromSeconds(10);
}
