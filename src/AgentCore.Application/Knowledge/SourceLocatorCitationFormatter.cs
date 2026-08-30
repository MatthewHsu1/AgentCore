using AgentCore.Domain.Knowledge;

namespace AgentCore.Application.Knowledge;

/// <summary>
/// Writes the source and the locator, joined by a comma: <c>&lt;source&gt;, &lt;locator&gt;</c>.
/// </summary>
/// <remarks>
/// The shipped formatter, and the one <c>providers.knowledge.citation</c> takes when a document
/// names none. It reads only the two roles <c>fields.source</c> and <c>fields.locator</c> map, so a
/// collection that maps neither cites nothing rather than citing something wrong.
/// </remarks>
public sealed class SourceLocatorCitationFormatter : IKnowledgeCitationFormatter
{
    /// <summary>The name <c>providers.knowledge.citation</c> selects this by.</summary>
    public const string FormatterName = "source-locator";

    /// <inheritdoc />
    public string Name => FormatterName;

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="card"/> is <see langword="null"/>.</exception>
    public string? Format(KnowledgeCard card)
    {
        ArgumentNullException.ThrowIfNull(card);

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

        return hasLocator ? card.SourceLocator : null;
    }
}
