using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Ports;
using AgentCore.TestSupport;

namespace AgentCore.AspNetCore.Tests.Fakes
{
    /// <summary>
    /// An in-memory store whose busy mark never holds anyone back, as when a holder's lease lapsed under it. Two turns
    /// through it race to the store's turn check, the backstop behind the mark. It is also its own adapter, of kind
    /// <c>test</c>.
    /// </summary>
    internal sealed class LapsedMarkStore() : DelegatingConversationStore(new InMemoryConversationStore()), IConversationStoreAdapter
    {
        public string Kind => "test";

        public ValueTask<IConversationStore> OpenAsync(
            VendorProviderConfiguration entry, ISecretResolverPort? secrets, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<IConversationStore>(this);
        }

        public override ValueTask<bool> TryMarkBusyAsync(
            string conversationId, string holder, TimeSpan lease, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(true);
        }
    }
}
