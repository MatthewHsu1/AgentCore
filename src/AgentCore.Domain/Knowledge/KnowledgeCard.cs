namespace AgentCore.Domain.Knowledge;

/// <summary>
/// One card the knowledge base returned, whole. It carries its own citation.
/// </summary>
public sealed record KnowledgeCard
{
    /// <summary>Gets the card id, such as <c>ct900-e33-incline-err</c>.</summary>
    public required string CardId { get; init; }

    /// <summary>Gets the card body, as the model will read it.</summary>
    public required string Text { get; init; }

    /// <summary>Gets how much the source is trusted: 3 a manual, 2 a note, 1 an email.</summary>
    public required int Authority { get; init; }

    /// <summary>Gets the manifest row this card came from, such as <c>ct900-om</c>.</summary>
    public required string SourceRef { get; init; }

    /// <summary>Gets where in that source it sits, such as <c>p.27</c>.</summary>
    public required string SourceLocator { get; init; }

    /// <summary>Gets the fused score, or <see langword="null"/> when a link pulled this card in.</summary>
    public double? Score { get; init; }

    /// <summary>Gets whether <c>see_also</c> pulled this card in rather than the ranking.</summary>
    public required bool ViaLink { get; init; }
}
