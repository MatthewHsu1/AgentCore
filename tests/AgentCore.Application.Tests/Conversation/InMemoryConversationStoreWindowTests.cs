using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Conversation
{
    /// <summary>Reading the message store a window of turns at a time, newest turns first, every turn whole.</summary>
    public sealed class InMemoryConversationStoreWindowTests
    {
        private static CancellationToken Token => TestContext.Current.CancellationToken;

        /// <summary>Four turns. Turn 2 answers through a tool, so it holds four rows rather than two.</summary>
        private static async Task<InMemoryConversationStore> FourTurnsAsync()
        {
            InMemoryConversationStore store = new();
            _ = await store.CreateAsync("c1", Token);
            _ = await store.AppendAsync(
                "c1",
                [
                    new ConversationMessageDraft(0, new ChatMessage(ChatRole.User, "q0"), "m0"),
                    new ConversationMessageDraft(0, new ChatMessage(ChatRole.Assistant, "a0"), "m1"),
                    new ConversationMessageDraft(1, new ChatMessage(ChatRole.User, "q1"), "m2"),
                    new ConversationMessageDraft(1, new ChatMessage(ChatRole.Assistant, "a1"), "m3"),
                    new ConversationMessageDraft(2, new ChatMessage(ChatRole.User, "q2"), "m4"),
                    new ConversationMessageDraft(2, new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call", "lookup")]), "m5"),
                    new ConversationMessageDraft(2, new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call", "found")]), "m6"),
                    new ConversationMessageDraft(2, new ChatMessage(ChatRole.Assistant, "a2"), "m7"),
                    new ConversationMessageDraft(3, new ChatMessage(ChatRole.User, "q3"), "m8"),
                    new ConversationMessageDraft(3, new ChatMessage(ChatRole.Assistant, "a3"), "m9"),
                ], cancellationToken: Token);
            return store;
        }

        [Fact]
        public async Task ReadWindowAsync_NoStart_ReadsTheNewestTurnsWholeOldestRowFirst()
        {
            InMemoryConversationStore store = await FourTurnsAsync();

            IReadOnlyList<ConversationMessage> rows = await store.ReadWindowAsync("c1", new TranscriptWindow(null, 2), Token);

            Assert.Equal(["m4", "m5", "m6", "m7", "m8", "m9"], rows.Select(row => row.MessageId));
        }

        [Fact]
        public async Task ReadWindowAsync_BeforeATurn_ReadsTheTurnsJustBelowIt()
        {
            InMemoryConversationStore store = await FourTurnsAsync();

            IReadOnlyList<ConversationMessage> rows = await store.ReadWindowAsync("c1", new TranscriptWindow(2, 2), Token);

            Assert.Equal(["m0", "m1", "m2", "m3"], rows.Select(row => row.MessageId));
        }

        [Fact]
        public async Task ReadWindowAsync_PastTheStart_ReadsWhatIsLeft()
        {
            InMemoryConversationStore store = await FourTurnsAsync();

            IReadOnlyList<ConversationMessage> rows = await store.ReadWindowAsync("c1", new TranscriptWindow(1, 5), Token);

            Assert.Equal(["m0", "m1"], rows.Select(row => row.MessageId));
        }

        [Fact]
        public async Task ReadWindowAsync_BeforeTheFirstTurn_ReadsNothing()
        {
            InMemoryConversationStore store = await FourTurnsAsync();

            IReadOnlyList<ConversationMessage> rows = await store.ReadWindowAsync("c1", new TranscriptWindow(0, 5), Token);

            Assert.Empty(rows);
        }
    }
}
