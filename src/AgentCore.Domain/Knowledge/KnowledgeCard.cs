namespace AgentCore.Domain.Knowledge;

/// <summary>
/// One card the knowledge base returned, whole. It carries its own citation.
/// </summary>
public sealed record KnowledgeCard
{
    /// <summary>The empty payload, for a mapper that carries nothing extra.</summary>
    private static readonly IReadOnlyDictionary<string, object?> NoExtras =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>Gets the card id, as the collection stores it.</summary>
    public required string CardId { get; init; }

    /// <summary>Gets the card body, as the model will read it.</summary>
    public required string Text { get; init; }

    /// <summary>Gets whether a link pulled this card in rather than the ranking.</summary>
    public required bool ViaLink { get; init; }

    /// <summary>Gets what this card came from, such as a document id. Empty when nothing maps it.</summary>
    public string SourceRef { get; init; } = string.Empty;

    /// <summary>Gets where in that source it sits, such as a page or a section.</summary>
    /// <remarks>Empty when the store maps no such field.</remarks>
    public string SourceLocator { get; init; } = string.Empty;

    /// <summary>Gets how much the source is trusted: higher is more trusted.</summary>
    public int? Authority { get; init; }

    /// <summary>Gets the retrieval score — cosine similarity under one leg, a fused rank score under several — or <see langword="null"/> when a link pulled this card in.</summary>
    public double? Score { get; init; }

    /// <summary>
    /// Gets whatever else the point carried, keyed the way the collection keys it.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Extras { get; init; } = NoExtras;
}
