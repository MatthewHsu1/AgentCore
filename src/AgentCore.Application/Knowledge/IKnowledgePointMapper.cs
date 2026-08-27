using AgentCore.Domain.Knowledge;

namespace AgentCore.Application.Knowledge;

/// <summary>
/// Turns one retrieved point into the card AgentCore injects, for collections whose card is not
/// expressible as field paths.
/// </summary>
public interface IKnowledgePointMapper
{
    /// <summary>Gets the name <c>providers.knowledge.mapper</c> selects this by.</summary>
    string Name { get; }

    /// <summary>Reads one point into a card, or <see langword="null"/> to skip the point.</summary>
    KnowledgeCard? Map(KnowledgePoint point);
}
