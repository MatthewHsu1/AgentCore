using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Transcript;
using AgentCore.TestSupport;
using Xunit;
using static AgentCore.Application.Tests.Transcript.ConversationSessionResumeTestSupport;

namespace AgentCore.Application.Tests.Transcript
{
    /// <summary>
    /// A conversation outlives the session that started it.
    /// </summary>
    public sealed class ConversationSessionResumeTests
    {
        [Fact]
        public async Task ASecondSessionOfOneConversation_SendsTheFirstTurnToTheModel()
        {
            InMemoryConversationStore store = new();
            string conversationId = await FirstTurnAsync(store, "my name is Dana", "Hello Dana");

            using ScriptedChatClient reply = new("Dana");
            using RequestCapturingChatClient capture = new(reply);
            ConversationSession resumed = CreateSession(OneAgentYaml, capture, store, conversationId);

            _ = await resumed.RunTurnAsync("what is my name?", TestContext.Current.CancellationToken);

            Assert.NotEmpty(capture.Requests);
            Assert.Contains(
                capture.Requests[^1],
                message => message.Text.Contains("my name is Dana", StringComparison.Ordinal));
        }

        [Fact]
        public async Task ASecondSessionOfOneConversation_KeepsEveryWordTheFirstOneWrote()
        {
            InMemoryConversationStore store = new();
            string conversationId = await FirstTurnAsync(store, "my name is Dana", "Hello Dana");

            await SecondTurnAsync(store, conversationId, "what is my name?", "Dana");

            IReadOnlyList<ConversationMessage> rows = await store.ReadAllAsync(conversationId, TestContext.Current.CancellationToken);

            // Two turns, each writing what the caller said and what it heard. A resumed session that
            // restarted its ordinals would overwrite the first pair rather than follow them.
            Assert.Equal(4, rows.Count);
            Assert.Equal([0, 1, 2, 3], rows.Select(row => row.Ordinal));
        }

        [Fact]
        public async Task ASecondSessionOfOneConversation_CountsItsTurnAsTheNextOne()
        {
            InMemoryConversationStore store = new();
            string conversationId = await FirstTurnAsync(store, "my name is Dana", "Hello Dana");

            await SecondTurnAsync(store, conversationId, "what is my name?", "Dana");

            IReadOnlyList<ConversationMessage> rows = await store.ReadAllAsync(conversationId, TestContext.Current.CancellationToken);

            // The turn index is the join to the audit chain, so a second turn numbered 0 would claim the
            // first turn's events as its own.
            Assert.Equal([0, 0, 1, 1], rows.Select(row => row.TurnIndex));
        }

        [Fact]
        public async Task ASessionOfAConversationWithNoWords_StartsAtTheBeginning()
        {
            InMemoryConversationStore store = new();
            _ = await store.CreateAsync("empty-conversation", TestContext.Current.CancellationToken);

            await SecondTurnAsync(store, "empty-conversation", "hello", "hi there");

            IReadOnlyList<ConversationMessage> rows = await store.ReadAllAsync("empty-conversation", TestContext.Current.CancellationToken);

            Assert.Equal([0, 1], rows.Select(row => row.Ordinal));
            Assert.Equal([0, 0], rows.Select(row => row.TurnIndex));
        }

        [Fact]
        public async Task ASecondSessionOfOneConversation_WhoseWordsCannotBeRead_RefusesTheTurn()
        {
            InMemoryConversationStore backing = new();
            UnreadableConversationStore store = new(backing);
            string conversationId = await FirstTurnAsync(store, "my name is Dana", "Hello Dana");

            using ScriptedChatClient reply = new("Dana");
            ConversationSession resumed = CreateSession(OneAgentYaml, reply, store, conversationId);

            // A read that answered empty would run the turn with no memory of the caller. Meeting a
            // stranger is worse than meeting an error, so the turn does not run at all.
            _ = await Assert.ThrowsAsync<InvalidOperationException>(
                () => resumed.RunTurnAsync("what is my name?", TestContext.Current.CancellationToken));
        }

        /// <summary>A store 1 that takes words and will not give them back.</summary>
        private sealed class UnreadableConversationStore(IConversationStore inner) : DelegatingConversationStore(inner)
        {
            public override ValueTask<IReadOnlyList<ConversationMessage>> ReadForSessionAsync(
                string conversationId, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("store 1 will not answer a read.");
            }
        }
    }
}
