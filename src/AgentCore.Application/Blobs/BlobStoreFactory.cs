using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Providers;

namespace AgentCore.Application.Blobs
{
    /// <summary>
    /// Opens the blob store the document names, from the adapters the host registered.
    /// </summary>
    public static class BlobStoreFactory
    {
        /// <summary>What this seam conversations itself, so the shared selector writes its failures.</summary>
        private static readonly VendorSeam Seam =
            new("providers.blobs", "/providers/blobs/kind", "options.UseBlobStores(...)", "stores");

        /// <summary>Opens the store <c>providers.blobs</c> names, or nothing.</summary>
        /// <param name="configuration">The loaded document.</param>
        /// <param name="secrets">The chain a credential resolves through, or <see langword="null"/>.</param>
        /// <param name="adapters">The vendors this host supports.</param>
        /// <param name="cancellationToken">Cancels the open.</param>
        /// <returns>The store, or <see langword="null"/> when the document names none.</returns>
        /// <exception cref="ArgumentNullException">The configuration or the adapters are <see langword="null"/>.</exception>
        /// <exception cref="ConfigurationLoadException">
        /// The document names a <c>kind</c> no adapter serves, or a <c>kind</c> two adapters answer to.
        /// </exception>
        public static async ValueTask<IBlobStore?> OpenAsync(
            AgentCoreConfiguration configuration,
            ISecretResolverPort? secrets,
            IReadOnlyList<IBlobStoreAdapter> adapters,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(adapters);

            if (configuration.Providers?.Blobs is not { } entry)
            {
                return null;
            }

            IBlobStoreAdapter adapter = VendorAdapterSelector.Select(entry.Kind, adapters, Seam);

            return await adapter.OpenAsync(entry, secrets, cancellationToken).ConfigureAwait(false);
        }
    }
}
