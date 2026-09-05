namespace AgentCore.Application.Knowledge;

/// <summary>One retrieved point, stripped of every vendor type.</summary>s
public sealed record KnowledgePoint
{
    /// <summary>Gets the store's own key for the point, as text. A numeric key is decimal digits.</summary>
    public required string PointId { get; init; }

    /// <summary>Gets the retrieval score — cosine similarity under one leg, a fused rank score under several — or <see langword="null"/> when the point was fetched rather than ranked.</summary>
    public double? Score { get; init; }

    /// <summary>Gets the payload, vendor-free.</summary>
    public required IReadOnlyDictionary<string, object?> Payload { get; init; }
}
