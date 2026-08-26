using AgentCore.Domain.Knowledge;
using Microsoft.Agents.AI;

namespace AgentCore.Application.Knowledge;

/// <summary>
/// Turns a card into what the framework injects, and applies <c>citations</c>.
/// </summary>
internal static class KnowledgeCardMapper
{
    /// <summary>Maps one card.</summary>
    /// <param name="card">The card the port returned.</param>
    /// <param name="citations">Whether the model may see the source label.</param>
    /// <returns>The result the framework formats into the turn.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="card"/> is <see langword="null"/>.</exception>
    internal static TextSearchProvider.TextSearchResult ToResult(KnowledgeCard card, bool citations)
    {
        ArgumentNullException.ThrowIfNull(card);

        return new TextSearchProvider.TextSearchResult
        {
            Text = card.Text,
            SourceName = citations ? $"{card.SourceRef}, {card.SourceLocator}" : null,
            SourceLink = null,
            RawRepresentation = card,
        };
    }
}
