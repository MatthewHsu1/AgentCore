using AgentCore.Application.Configuration.Schema;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Ports;

/// <summary>
/// Builds the vendor client behind one <c>providers.llm[].kind</c> value.
/// </summary>
public interface IChatClientAdapter : IVendorAdapter
{
    /// <summary>Builds the vendor client of one entry.</summary>
    /// <param name="entry">The <c>providers.llm[]</c> entry, whose <c>kind</c> named this adapter.</param>
    /// <param name="secrets">The chain a credential resolves through, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the build.</param>
    /// <returns>The client. The composite owns and disposes it; the adapter never does.</returns>
    ValueTask<IChatClient> CreateClientAsync(
        LlmProviderConfiguration entry,
        ISecretResolverPort? secrets,
        CancellationToken cancellationToken = default);

    /// <summary>Whether this vendor runs a hosted web search for one entry.</summary>
    /// <param name="entry">The <c>providers.llm[]</c> entry this adapter serves.</param>
    /// <returns><see langword="true"/> when the vendor runs the search itself.</returns>
    bool SupportsHostedWebSearch(LlmProviderConfiguration entry) => false;
}
