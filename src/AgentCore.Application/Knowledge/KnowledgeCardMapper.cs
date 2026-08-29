using AgentCore.Domain.Knowledge;
using Microsoft.Agents.AI;

namespace AgentCore.Application.Knowledge;

/// <summary>
/// Turns a card into what the framework injects, and applies <c>citations</c>.
/// </summary>
internal static class KnowledgeCardMapper
{
    /// <summary>Maps one card.</summary>
    /// <param name="card">The card the store returned.</param>
    /// <param name="citations">Whether this agent shows the model where the card came from.</param>
    /// <param name="formatter">The wording <c>providers.knowledge.citation</c> named.</param>
    /// <returns>The result the framework injects.</returns>
    internal static TextSearchProvider.TextSearchResult ToResult(
        KnowledgeCard card, bool citations, IKnowledgeCitationFormatter formatter)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(formatter);

        return new TextSearchProvider.TextSearchResult
        {
            Text = card.Text,

            // An empty label is not a label. The framework renders whatever is here, so a formatter
            // that returns "" would put a bare separator in front of the model instead of nothing.
            SourceName = citations ? Trimmed(formatter.Format(card)) : null,
            SourceLink = null,
            RawRepresentation = card,
        };
    }

    /// <summary>Reads a formatter's answer, treating blank as no citation.</summary>
    private static string? Trimmed(string? label)
        => label is { Length: > 0 } && label.Trim().Length > 0 ? label : null;
}
