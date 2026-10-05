using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Ports;
using AgentCore.TestSupport;

namespace AgentCore.AspNetCore.Tests.Fakes
{
    /// <summary>
    /// An in-memory store, and its own adapter of kind <c>test</c>, whose first conversation create waits until the test
    /// opens the gate.
    /// </summary>
    internal sealed class GatedCreateStore() : DelegatingConversationStore(new InMemoryConversationStore()), IConversationStoreAdapter
    {
        private int _gated;

        /// <summary>Completes when the first create reached the gate.</summary>
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Opens the gate.</summary>
        public TaskCompletionSource Open { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Kind => "test";

        public ValueTask<IConversationStore> OpenAsync(
            VendorProviderConfiguration entry, ISecretResolverPort? secrets, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<IConversationStore>(this);
        }

        public override async ValueTask<ConversationRecord> CreateAsync(string conversationId, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _gated, 1) == 0)
            {
                _ = Entered.TrySetResult();
                await Open.Task.WaitAsync(cancellationToken);
            }

            return await base.CreateAsync(conversationId, cancellationToken);
        }
    }
}
