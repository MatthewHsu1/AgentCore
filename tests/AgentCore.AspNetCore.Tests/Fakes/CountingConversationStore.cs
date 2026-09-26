using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using AgentCore.TestSupport;

namespace AgentCore.AspNetCore.Tests.Fakes
{
    /// <summary>
    /// A conversation store that counts every call to <see cref="ReadForSessionAsync"/>: the one read
    /// that returns the whole transcript a session opens on. Everything else delegates to a real
    /// in-memory store, so a test can seed rows and file continuations exactly as a live store would.
    /// </summary>
    internal sealed class CountingConversationStore()
        : DelegatingConversationStore(new InMemoryConversationStore()), IConversationStoreAdapter
    {
        private int _fullReads;

        public string Kind => "test";

        /// <summary>Gets how many times <see cref="ReadForSessionAsync"/> was called.</summary>
        public int FullReads => _fullReads;

        public ValueTask<IConversationStore> OpenAsync(
            VendorProviderConfiguration entry, ISecretResolverPort? secrets, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<IConversationStore>(this);
        }

        public override async ValueTask<IReadOnlyList<ConversationMessage>> ReadForSessionAsync(
            string conversationId, CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref _fullReads);
            return await base.ReadForSessionAsync(conversationId, cancellationToken).ConfigureAwait(false);
        }
    }
}
