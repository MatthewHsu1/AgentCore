using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Ports;
using AgentCore.TestSupport;

namespace AgentCore.AspNetCore.Tests.Fakes
{
    /// <summary>An in-memory store, and its own adapter of kind <c>test</c>, whose conversation create throws while <see cref="Failing"/> is set.</summary>
    internal sealed class FailingCreateStore() : DelegatingConversationStore(new InMemoryConversationStore()), IConversationStoreAdapter
    {
        private volatile bool _failing = true;

        public bool Failing
        {
            get => _failing;
            set => _failing = value;
        }

        public string Kind => "test";

        public ValueTask<IConversationStore> OpenAsync(
            VendorProviderConfiguration entry, ISecretResolverPort? secrets, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<IConversationStore>(this);
        }

        public override ValueTask<ConversationRecord> CreateAsync(string conversationId, CancellationToken cancellationToken = default)
        {
            return _failing
                ? throw new InvalidOperationException("the store is down")
                : base.CreateAsync(conversationId, cancellationToken);
        }
    }
}
