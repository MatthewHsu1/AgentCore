using AgentCore.Domain.Knowledge;

namespace AgentCore.Application.Ports;

/// <summary>
/// Reads whole cards by one exact facet value.
/// </summary>
/// <remarks>
/// This is the keyword half of the knowledge base, beside <see cref="IKnowledgeRetrievalPort"/>'s
/// similarity half. A value a caller can name exactly — a model year, a section, a brand — is not
/// something ranking answers well: two adjacent years read as near-identical to an embedding, and
/// the wrong one wins. A store that cannot filter does not implement this interface, and a caller
/// asks for it through <see cref="IKnowledgeRetrievalPort.GetService"/>.
/// </remarks>
public interface IKnowledgeFacetReadPort
{
    /// <summary>Reads the cards carrying one value at one payload path.</summary>
    /// <param name="path">The payload path, already resolved from <c>scope.template</c>.</param>
    /// <param name="value">The value the cards must carry at that path, matched exactly.</param>
    /// <param name="limit">The most cards to return.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The cards, in no defined order. Empty when nothing carries the value, which is not an error.</returns>
    ValueTask<IReadOnlyList<KnowledgeCard>> ReadByFacetAsync(
        string path,
        string value,
        int limit,
        CancellationToken cancellationToken = default);
}
