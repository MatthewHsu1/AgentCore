using AgentCore.Domain.Knowledge;

namespace AgentCore.Application.Ports;

/// <summary>
/// The whole knowledge base, behind one method.
/// </summary>
public interface IKnowledgeRetrievalPort
{
    /// <summary>Finds the cards that answer one query.</summary>
    /// <param name="query">What the caller asked, in their own words.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The cards, best first. It is empty when nothing clears the score floor.</returns>
    ValueTask<IReadOnlyList<KnowledgeCard>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default);
}
