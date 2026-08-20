using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.Ports;

/// <summary>
/// Opens the store 1 backing behind one <c>providers.transcript</c> value.
/// </summary>
public interface ITranscriptStoreAdapter : IVendorAdapter
{
    /// <summary>Opens the store this vendor writes to, and hands it over.</summary>
    /// <param name="entry">The <c>providers.transcript</c> block, whose <c>kind</c> named this adapter.</param>
    /// <param name="secrets">The chain a credential resolves through, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The store, which the caller owns for the life of the process.</returns>
    /// <remarks>
    /// This runs once, while the host starts, so a missing credential stops the host and never a call.
    /// </remarks>
    ValueTask<ITranscriptStore> OpenAsync(
        VendorProviderConfiguration entry,
        ISecretResolverPort? secrets,
        CancellationToken cancellationToken = default);
}
