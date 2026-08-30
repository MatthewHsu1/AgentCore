using AgentCore.Domain.Knowledge;

namespace AgentCore.Application.Knowledge;

/// <summary>
/// Turns one card into the source label the model is shown beside it.
/// </summary>
/// <remarks>
/// A citation is a sentence a deployment says about its own documents, so its wording belongs to
/// the deployment. This seam is how it says it: read whichever fields the collection carries,
/// including anything in <see cref="KnowledgeCard.Extras"/>, and return the label.
/// </remarks>
public interface IKnowledgeCitationFormatter
{
    /// <summary>Gets the name <c>providers.knowledge.citation</c> selects this by.</summary>
    string Name { get; }

    /// <summary>Reads one card's source label, or <see langword="null"/> to cite nothing for it.</summary>
    /// <param name="card">The card about to be shown to the model.</param>
    /// <returns>The label, or <see langword="null"/>.</returns>
    string? Format(KnowledgeCard card);
}
