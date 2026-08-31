using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.Ports;

/// <summary>
/// Opens the store 0 backing behind one <c>providers.calls</c> value.
/// </summary>
public interface ICallStoreAdapter : IVendorAdapter
{
    /// <summary>Opens the store this vendor writes to, and hands it over.</summary>
    /// <param name="entry">The <c>providers.calls</c> block, whose <c>kind</c> named this adapter.</param>
    /// <param name="secrets">The chain a credential resolves through, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    ValueTask<ICallStore> OpenAsync(
        VendorProviderConfiguration entry,
        ISecretResolverPort? secrets,
        CancellationToken cancellationToken = default);
}
