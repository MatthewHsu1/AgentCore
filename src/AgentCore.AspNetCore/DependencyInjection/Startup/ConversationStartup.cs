using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.DependencyInjection.Startup
{
    /// <summary>Step 4c: open the store 0 backing the document names, before the document is compiled.</summary>
    internal static class ConversationStartup
    {
        /// <summary>Opens the store <c>providers.conversations</c> names, or the built-in one.</summary>
        /// <param name="configuration">The loaded document. It carries <c>providers.conversations</c>.</param>
        /// <param name="options">The options the host filled. It carries the conversation store vendors.</param>
        /// <param name="loggers">The factory the defaulting warning is written through.</param>
        /// <param name="cancellationToken">Cancels the store open.</param>
        /// <returns>The store, which is never <see langword="null"/>.</returns>
        internal static async ValueTask<IConversationStore> OpenAsync(
            AgentCoreConfiguration configuration,
            AgentCoreOptions options,
            ILoggerFactory loggers,
            CancellationToken cancellationToken)
        {
            IConversationStore store = await ConversationStoreFactory
                .OpenAsync(
                    configuration,
                    options.SecretResolver,
                    options.ConversationStores ?? [],
                    cancellationToken)
                .ConfigureAwait(false);

            if (store is InMemoryConversationStore && configuration.Providers?.Conversations is null)
            {
                StartupLog.ConversationStoreDefaulted(loggers.CreateLogger<InMemoryConversationStore>());
            }

            return store;
        }
    }
}
