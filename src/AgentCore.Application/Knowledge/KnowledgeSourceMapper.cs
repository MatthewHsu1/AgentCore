using AgentCore.Domain.Knowledge;
using AgentCore.Domain.Sources;

namespace AgentCore.Application.Knowledge;

/// <summary>
/// Turns a card into the chip the caller sees, using the same label the model was shown.
/// </summary>
internal static class KnowledgeSourceMapper
{
    /// <summary>What <see cref="SourceReference.Origin"/> says for a card. A wire value.</summary>
    internal const string KnowledgeOrigin = "knowledge";

    /// <summary>Maps one card, or cites nothing for it.</summary>
    /// <param name="card">The card the store returned.</param>
    /// <param name="formatter">The wording <c>providers.knowledge.citation</c> named.</param>
    /// <returns>The source, or <see langword="null"/> when the formatter cites nothing.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    internal static SourceReference? ToSource(KnowledgeCard card, IKnowledgeCitationFormatter formatter)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(formatter);

        var label = formatter.Format(card);

        if (label is not { Length: > 0 } || label.Trim().Length == 0)
        {
            return null;
        }

        return new SourceReference
        {
            SourceId = card.CardId,
            Kind = SourceKind.Document,
            Title = label,
            Origin = KnowledgeOrigin,
            Locator = card.SourceLocator,
        };
    }
}
