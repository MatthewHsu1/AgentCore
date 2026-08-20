using AgentCore.Application.Configuration.Schema;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Ports;

/// <summary>
/// Builds the vendor client behind one <c>providers.llm[].kind</c> value.
/// </summary>
/// <remarks>
/// <para>
/// The document names a vendor in <c>kind</c> and the host registers one adapter for each vendor it
/// supports. <c>CompositeChatClientFactory</c> routes each entry to the adapter whose
/// <see cref="IVendorAdapter.Kind"/> matches, so a document that changes vendors changes no code.
/// </para>
/// <para>
/// An adapter owns its vendor only: the SDK client, the credential, the model name. Everything
/// vendor-neutral — the <c>as</c> map, the default entry, the temperature wrapper, the cache — lives
/// in the composite, so no adapter repeats it.
/// </para>
/// </remarks>
public interface IChatClientAdapter : IVendorAdapter
{
    /// <summary>Builds the vendor client of one entry.</summary>
    /// <param name="entry">The <c>providers.llm[]</c> entry, whose <c>kind</c> named this adapter.</param>
    /// <param name="secrets">The chain a credential resolves through, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the build.</param>
    /// <returns>The client. The composite owns and disposes it; the adapter never does.</returns>
    /// <remarks>
    /// This runs once for each <c>as</c> name, while the host starts. A bad credential therefore
    /// stops the host, never a call.
    /// </remarks>
    ValueTask<IChatClient> CreateClientAsync(
        LlmProviderConfiguration entry,
        ISecretResolverPort? secrets,
        CancellationToken cancellationToken = default);
}
