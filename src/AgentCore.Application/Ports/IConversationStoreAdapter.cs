using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.Ports;

/// <summary>
/// Opens the store 0 backing behind one <c>providers.conversations</c> value.
/// </summary>
public interface IConversationStoreAdapter : IVendorAdapter
{
    /// <summary>Opens the store this vendor writes to, and hands it over.</summary>
    /// <param name="entry">The <c>providers.conversations</c> block, whose <c>kind</c> named this adapter.</param>
    /// <param name="secrets">The chain a credential resolves through, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    ValueTask<IConversationStore> OpenAsync(
        VendorProviderConfiguration entry,
        ISecretResolverPort? secrets,
        CancellationToken cancellationToken = default);
}
