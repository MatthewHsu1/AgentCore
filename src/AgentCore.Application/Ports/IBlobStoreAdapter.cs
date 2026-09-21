using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.Ports
{
    /// <summary>
    /// Opens the blob store behind one <c>providers.blobs</c> value.
    /// </summary>
    public interface IBlobStoreAdapter : IVendorAdapter
    {
        /// <summary>Opens the store this vendor writes to, and hands it over.</summary>
        /// <param name="entry">The <c>providers.blobs</c> block, whose <c>kind</c> named this adapter.</param>
        /// <param name="secrets">The chain a credential resolves through, or <see langword="null"/>.</param>
        /// <param name="cancellationToken">Cancels the open.</param>
        ValueTask<IBlobStore> OpenAsync(
            BlobProviderConfiguration entry,
            ISecretResolverPort? secrets,
            CancellationToken cancellationToken = default);
    }
}
