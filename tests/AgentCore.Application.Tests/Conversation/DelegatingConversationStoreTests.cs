using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Conversation
{
    /// <summary>The forwarding base every store fake is built on.</summary>
    public sealed class DelegatingConversationStoreTests
    {
        private static CancellationToken Token => TestContext.Current.CancellationToken;

        private sealed class CountingAppends(IConversationStore inner) : DelegatingConversationStore(inner)
        {
            public int Appends { get; private set; }

            public override ValueTask<IReadOnlyList<ConversationMessage>> AppendAsync(
                string conversationId,
                IReadOnlyList<ConversationMessageDraft> messages,
                ConversationSessionState? state = null,
                CancellationToken cancellationToken = default)
            {
                Appends++;
                return base.AppendAsync(conversationId, messages, state, cancellationToken);
            }
        }

        [Fact]
        public async Task AnOverride_CountsItsOwnCalls_AndStillReachesTheInnerStore()
        {
            // Arrange
            CountingAppends store = new(new InMemoryConversationStore());
            _ = await store.CreateAsync("c1", Token);

            // Act
            _ = await store.AppendAsync(
                "c1", [new ConversationMessageDraft(0, new ChatMessage(ChatRole.User, "hi"), "m0")], cancellationToken: Token);

            // Assert
            Assert.Equal(1, store.Appends);
            _ = Assert.Single(await store.ReadAllAsync("c1", Token));
        }

        [Fact]
        public async Task AMethodNotOverridden_ReachesTheInnerStoreUnchanged()
        {
            // Arrange
            CountingAppends store = new(new InMemoryConversationStore());

            // Act
            ConversationRecord conversation = await store.CreateAsync("c1", Token);

            // Assert
            Assert.Equal("c1", conversation.ConversationId);
            Assert.Equal(0, store.Appends);
        }
    }
}
