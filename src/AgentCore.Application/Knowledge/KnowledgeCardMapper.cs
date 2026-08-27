using AgentCore.Domain.Knowledge;
using Microsoft.Agents.AI;

namespace AgentCore.Application.Knowledge;

/// <summary>
/// Turns a card into what the framework injects, and applies <c>citations</c>.
/// </summary>
internal static class KnowledgeCardMapper
{
    /// <summary>Maps one card.</summary>
    internal static TextSearchProvider.TextSearchResult ToResult(KnowledgeCard card, bool citations)
    {
        ArgumentNullException.ThrowIfNull(card);

        return new TextSearchProvider.TextSearchResult
        {
            Text = card.Text,
            SourceName = citations ? SourceName(card) : null,
            SourceLink = null,
            RawRepresentation = card,
        };
    }

    private static string? SourceName(KnowledgeCard card)
    {
        var hasRef = card.SourceRef.Length > 0;
        var hasLocator = card.SourceLocator.Length > 0;

        if (hasRef && hasLocator)
        {
            return $"{card.SourceRef}, {card.SourceLocator}";
        }

        if (hasRef)
        {
            return card.SourceRef;
        }

        if (hasLocator)
        {
            return card.SourceLocator;
        }

        return null;
    }
}
