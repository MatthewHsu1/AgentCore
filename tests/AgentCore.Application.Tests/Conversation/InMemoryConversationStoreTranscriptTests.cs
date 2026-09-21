using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Transcript;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Conversation
{
    /// <summary>The words half of store 0, now that one store holds both halves.</summary>
    public sealed class InMemoryConversationStoreTranscriptTests
    {
        private static CancellationToken Token => TestContext.Current.CancellationToken;

        [Fact]
        public async Task RewriteAsync_AnExistingMessage_ReplacesItsContent()
        {
            // Arrange
            InMemoryConversationStore store = new();
            _ = await store.CreateAsync("c1", Token);
            _ = await store.AppendAsync(
                "c1",
                [new ConversationMessageDraft(0, new ChatMessage(ChatRole.Assistant, "long reply"), "m0")],
                cancellationToken: Token);

            // Act
            await store.RewriteAsync("c1", "m0", new ChatMessage(ChatRole.Assistant, "cut"), Token);

            // Assert
            IReadOnlyList<ConversationMessage> rows = await store.ReadAllAsync("c1", Token);
            Assert.Equal("cut", Assert.Single(rows).Content.Text);
        }

        [Fact]
        public async Task EraseAsync_AConversationWithWords_RemovesThemAndReportsTheCount()
        {
            // Arrange
            InMemoryConversationStore store = new();
            _ = await store.CreateAsync("c1", Token);
            _ = await store.CreateAsync("c2", Token);
            _ = await store.AppendAsync(
                "c1", [new ConversationMessageDraft(0, new ChatMessage(ChatRole.User, "a"), "m0")], cancellationToken: Token);
            _ = await store.AppendAsync(
                "c2", [new ConversationMessageDraft(0, new ChatMessage(ChatRole.User, "b"), "m0")], cancellationToken: Token);

            // Act
            int erased = await store.EraseAsync("c1", Token);

            // Assert
            Assert.Equal(1, erased);
            Assert.Empty(await store.ReadAllAsync("c1", Token));
            _ = Assert.Single(await store.ReadAllAsync("c2", Token));
        }

        [Fact]
        public async Task GetAsync_AfterAppend_ReportsWhenTheConversationWasLastSpokenOn()
        {
            // Arrange
            InMemoryConversationStore store = new();
            _ = await store.CreateAsync("c1", Token);

            // Act
            _ = await store.AppendAsync(
                "c1", [new ConversationMessageDraft(0, new ChatMessage(ChatRole.User, "hello"), "m0")], cancellationToken: Token);

            // Assert
            ConversationRecord? conversation = await store.GetAsync("c1", Token);
            Assert.NotNull(conversation);
            _ = Assert.NotNull(conversation.LastMessageAt);
        }

        [Fact]
        public async Task EraseAsync_AConversation_TakesEveryRowAndReportsHowMany()
        {
            // Arrange
            InMemoryConversationStore store = new();
            _ = await store.CreateAsync("c1", Token);
            _ = await store.AppendAsync(
                "c1",
                [
                    new ConversationMessageDraft(0, new ChatMessage(ChatRole.User, "one"), "m0"),
                    new ConversationMessageDraft(0, new ChatMessage(ChatRole.Assistant, "two"), "m1"),
                ],
                cancellationToken: Token);

            // Act
            int erased = await store.EraseAsync("c1", Token);

            // Assert
            Assert.Equal(2, erased);
            Assert.Empty(await store.ReadAllAsync("c1", Token));
        }

        [Fact]
        public async Task EraseAsync_AConversation_LeavesEveryOtherConversationAlone()
        {
            // Arrange
            InMemoryConversationStore store = new();
            _ = await store.CreateAsync("c1", Token);
            _ = await store.CreateAsync("c2", Token);
            _ = await store.AppendAsync(
                "c1", [new ConversationMessageDraft(0, new ChatMessage(ChatRole.User, "mine"), "m0")], cancellationToken: Token);
            _ = await store.AppendAsync(
                "c2", [new ConversationMessageDraft(0, new ChatMessage(ChatRole.User, "theirs"), "m0")], cancellationToken: Token);

            // Act
            _ = await store.EraseAsync("c1", Token);

            // Assert
            _ = Assert.Single(await store.ReadAllAsync("c2", Token));
        }
    }
}
