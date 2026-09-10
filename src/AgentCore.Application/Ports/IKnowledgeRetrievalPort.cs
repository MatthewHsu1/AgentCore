using AgentCore.Domain.Knowledge;

namespace AgentCore.Application.Ports;

/// <summary>
/// The whole knowledge base, behind one method — and a way to ask for the rest.
/// </summary>
/// <remarks>
/// Search is the one thing every store owes. Everything beyond it — reading a facet vocabulary,
/// reading whole cards by an exact facet value — is a capability a store may or may not have, and
/// it lives on its own interface that only a store able to serve it implements. A caller asks for
/// one through <see cref="GetService"/>.
/// </remarks>
public interface IKnowledgeRetrievalPort
{
    /// <summary>Finds the cards that answer one query.</summary>
    /// <param name="query">What the caller asked, in their own words.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The cards, best first. It is empty when nothing clears the score floor.</returns>
    ValueTask<IReadOnlyList<KnowledgeCard>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default);

    /// <summary>Asks this store for a capability it may also serve.</summary>
    /// <param name="serviceType">The capability being asked for, such as <see cref="IFacetVocabularyPort"/>.</param>
    /// <param name="serviceKey">Names one of several, when a store serves more than one. Most serve none.</param>
    /// <returns>The capability, or <see langword="null"/> when this store does not serve it.</returns>
    /// <remarks>
    /// The default answers for a store that is whatever it implements, which is every store that
    /// wraps nothing. A store that wraps another overrides this and forwards what it cannot answer,
    /// which is the whole reason this is a method and not a cast: a wrapper does not implement what
    /// it wraps, so <c>is</c> loses the inner store's capabilities and this does not.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="serviceType"/> is <see langword="null"/>.</exception>
    object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }
}
