using System.Reflection;
using Microsoft.Agents.AI;

namespace AgentCore.TestSupport;

/// <summary>
/// Reaches the search delegate a <see cref="TextSearchProvider"/> was built around.
/// </summary>
/// <remarks>
/// The delegate is the only way to drive a bound provider uniformly across both
/// <c>SearchTime</c> modes: under <c>OnDemandFunctionCalling</c> the framework never calls it before
/// the model does, and the provider's own <c>SearchAsync</c> wraps the answer into a formatted
/// string rather than the raw result list a test needs to inspect. It lives behind a private field,
/// so this is reflection — verified against <c>Microsoft.Agents.AI</c> 1.17.0.
/// </remarks>
public static class TextSearchProviderInternals
{
    /// <summary>Reads the provider's own search delegate.</summary>
    /// <param name="provider">A provider built by the knowledge factory.</param>
    /// <returns>The delegate, ready to call.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="provider"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="provider"/> is not a <see cref="TextSearchProvider"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// <see cref="TextSearchProvider"/> no longer declares the private field this depends on — a newer
    /// <c>Microsoft.Agents.AI</c> changed its internals, and this helper needs updating for the new
    /// shape rather than every caller seeing a bare <see cref="NullReferenceException"/>.
    /// </exception>
    public static Func<string, CancellationToken, Task<IEnumerable<TextSearchProvider.TextSearchResult>>> SearchDelegate(
        AIContextProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        if (provider is not TextSearchProvider search)
        {
            throw new ArgumentException(
                $"expected a {nameof(TextSearchProvider)}, not a {provider.GetType().Name}.", nameof(provider));
        }

        var field = typeof(TextSearchProvider).GetField("_searchAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "TextSearchProvider no longer has a private field named '_searchAsync'. "
                + "Microsoft.Agents.AI changed its internals; update TextSearchProviderInternals for the new shape.");

        return (Func<string, CancellationToken, Task<IEnumerable<TextSearchProvider.TextSearchResult>>>)
            field.GetValue(search)!;
    }

    /// <summary>Runs the provider's own search delegate and collects what it returned.</summary>
    /// <param name="provider">A provider built by the knowledge factory.</param>
    /// <param name="query">The search text.</param>
    /// <param name="cancellationToken">The token the delegate receives, as a caller's own would be.</param>
    /// <returns>What the delegate returned.</returns>
    public static async Task<IReadOnlyList<TextSearchProvider.TextSearchResult>> SearchAsync(
        AIContextProvider provider,
        string query,
        CancellationToken cancellationToken = default)
        => [.. await SearchDelegate(provider)(query, cancellationToken).ConfigureAwait(false)];
}
