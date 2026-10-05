using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Providers;

namespace AgentCore.Application.Conversation
{
    /// <summary>
    /// Opens the conversation store the document names, from the adapters the host registered.
    /// </summary>
    public static class ConversationStoreFactory
    {
        /// <summary>The built-in kind, and the one a document that names no provider gets.</summary>
        public const string MemoryKind = "memory";

        /// <summary>What this seam conversations itself, so the shared selector writes its failures.</summary>
        private static readonly VendorSeam Seam =
            new("providers.conversations", "/providers/conversations/kind", "options.UseConversationStores(...)", "stores");

        /// <summary>Opens the store <c>providers.conversations</c> names, or the built-in one.</summary>
        /// <param name="configuration">The loaded document.</param>
        /// <param name="secrets">The chain a credential resolves through, or <see langword="null"/>.</param>
        /// <param name="adapters">The vendors this host supports.</param>
        /// <param name="cancellationToken">Cancels the open.</param>
        /// <returns>The store, which is never <see langword="null"/>.</returns>
        /// <exception cref="ArgumentNullException">The configuration or the adapters are <see langword="null"/>.</exception>
        /// <exception cref="ConfigurationLoadException">
        /// The document names a <c>kind</c> no adapter serves, or a <c>kind</c> two adapters answer to.
        /// </exception>
        public static ValueTask<IConversationStore> OpenAsync(
            AgentCoreConfiguration configuration,
            ISecretResolverPort? secrets,
            IReadOnlyList<IConversationStoreAdapter> adapters,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(adapters);

            if (configuration.Providers?.Conversations is not { } entry
                || string.Equals(entry.Kind, MemoryKind, StringComparison.OrdinalIgnoreCase))
            {
                return ValueTask.FromResult<IConversationStore>(new InMemoryConversationStore());
            }

            IConversationStoreAdapter adapter = VendorAdapterSelector.Select(entry.Kind, adapters, Seam);
            return adapter.OpenAsync(entry, secrets, cancellationToken);
        }
    }
}
