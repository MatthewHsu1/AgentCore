using AgentCore.Application.Blobs;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;

namespace AgentCore.AspNetCore.DependencyInjection.Startup;

/// <summary>Step 4d: open the blob store the document names, before the document is compiled.</summary>
internal static class BlobStartup
{
    /// <summary>Opens the store <c>providers.blobs</c> names, or nothing.</summary>
    /// <param name="configuration">The loaded document. It carries <c>providers.blobs</c>.</param>
    /// <param name="options">The options the host filled. It carries the blob store vendors.</param>
    /// <param name="cancellationToken">Cancels the store open.</param>
    /// <returns>The store, or <see langword="null"/> when the document names none.</returns>
    internal static ValueTask<IBlobStore?> OpenAsync(
        AgentCoreConfiguration configuration,
        AgentCoreOptions options,
        CancellationToken cancellationToken)
        => BlobStoreFactory.OpenAsync(
            configuration,
            options.SecretResolver,
            options.BlobStores ?? [],
            cancellationToken);
}
