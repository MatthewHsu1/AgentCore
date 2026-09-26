using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Transcript;
using AgentCore.TestSupport;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using static AgentCore.Application.Tests.Transcript.AgentCoreChatHistoryProviderTestSupport;

namespace AgentCore.Application.Tests.Transcript
{
    /// <summary>
    /// Pins what a barge-in does to the words the provider already wrote: which row it rewrites, and
    /// when the rewrite is guaranteed to land after the append it corrects.
    /// </summary>
    public sealed class AgentCoreChatHistoryProviderTruncateTests
    {
        [Fact]
        public async Task RewriteReply_LastAssistantMessage_RewritesOnlyThatMessage()
        {
            // Arrange
            (AgentCoreChatHistoryProvider? provider, Fakes.RecordingConversationStore? store, StubSession? session) = await NewConversation();
            AppendTurn(provider, session, turnIndex: 0, "hello", "hi there");
            AppendTurn(provider, session, turnIndex: 1, "order 41?", "it ships Friday from the depot");

            // Act
            bool cut = provider.RewriteReply(session, 1, "it ships");

            // Assert
            Assert.True(cut);
            IReadOnlyList<ChatMessage> history = await ProvideAsync(provider, session);
            Assert.Equal(
                ["hello", "hi there", "order 41?", "it ships"],
                history.Select(message => message.Text));
            await provider.DrainAsync(session);
            Assert.Equal([3], store.Rewrites.Select(rewrite => rewrite.Ordinal));
        }

        [Fact]
        public async Task ProvideChatHistory_AfterTruncate_ReturnsHeardTextNotProducedText()
        {
            // Arrange
            (AgentCoreChatHistoryProvider? provider, Fakes.RecordingConversationStore _, StubSession? session) = await NewConversation();
            AppendTurn(provider, session, turnIndex: 0, "order 41?", "it ships Friday from the depot");

            // Act
            _ = provider.RewriteReply(session, 0, "it ships Fri");

            // Assert
            IReadOnlyList<ChatMessage> history = await ProvideAsync(provider, session);
            Assert.Equal(["order 41?", "it ships Fri"], history.Select(message => message.Text));
        }

        /// <summary>
        /// The barge-in lands while the turn it belongs to is still inside its own write. The write
        /// chain is what orders the two: a rewrite that overtook the insert it targets would find no row
        /// and leave the record holding words the caller never heard.
        /// </summary>
        [Fact]
        public async Task RewriteReply_WhileTheAppendIsStillWriting_ReachesTheStoreAfterIt()
        {
            // Arrange
            BlockingConversationStore store = new();
            _ = await store.CreateAsync(ConversationId, TestContext.Current.CancellationToken);
            AgentCoreChatHistoryProvider provider = new(store);
            StubSession session = new();
            _ = provider.BeginConversation(session, ConversationId, []);
            AppendTurn(provider, session, turnIndex: 0, "hello", "hi there");
            await provider.DrainAsync(session);
            store.BlockNextAppend();
            AppendTurn(provider, session, turnIndex: 1, "order 41?", "it ships Friday from the depot");
            await store.Entered;

            // Act
            bool cut = provider.RewriteReply(session, 1, "it ships");
            store.Release();
            await provider.DrainAsync(session);

            // Assert
            Assert.True(cut);
            Assert.Equal(
                ["hello", "hi there", "order 41?", "it ships"],
                (await store.ReadAllAsync(ConversationId, TestContext.Current.CancellationToken)).Select(row => row.Content.Text));
        }

        /// <summary>
        /// A model routinely writes a line and puts the tool call it announces on the same message, then
        /// answers in a second step. The first step's words stay before their tool call and only the second
        /// step is cut to what was heard (owner ruling 2026-09-23).
        /// </summary>
        [Fact]
        public async Task RewriteReply_TurnWithProseBesideAToolCall_KeepsTheProseAndCutsTheLastStep()
        {
            // Arrange
            (AgentCoreChatHistoryProvider? provider, Fakes.RecordingConversationStore? store, StubSession? session) = await NewConversation();
            provider.BeginTurn(session, turnIndex: 0);
            ChatMessage announced = new(
                ChatRole.Assistant,
                [new TextContent("Let me check that for you"), new FunctionCallContent("conversation-1", "lookup")]);
            _ = provider.CommitTurn(
                session,
                new TurnCommit(new ChatMessage(ChatRole.User, "how much?"))
                {
                    Seen = new AgentResponse(
                    [
                        announced,
                        new ChatMessage(ChatRole.Tool, [new FunctionResultContent("conversation-1", "50")]),
                        new ChatMessage(ChatRole.Assistant, "the price is fifty"),
                    ]),
                });

            // Act
            bool cut = provider.RewriteReply(session, 0, "Let me check that for youthe price");

            // Assert
            Assert.True(cut);
            IReadOnlyList<ChatMessage> history = await ProvideAsync(provider, session);
            Assert.Equal(["how much?", "Let me check that for you", string.Empty, "the price"], history.Select(m => m.Text));

            // The side effect ran, so the pair stays. That is the rule the cut must not break.
            Assert.Contains(history, m => m.Contents.OfType<FunctionCallContent>().Any());
            Assert.Contains(history, m => m.Contents.OfType<FunctionResultContent>().Any());

            await provider.DrainAsync(session);
            Assert.Equal([3], store.Rewrites.Select(rewrite => rewrite.Ordinal));
        }

        [Fact]
        public async Task RewriteReply_BeforeAnyReplyExists_NoOps()
        {
            // Arrange
            (AgentCoreChatHistoryProvider? provider, Fakes.RecordingConversationStore? store, StubSession? session) = await NewConversation();
            provider.BeginTurn(session, turnIndex: 0);

            // Act
            bool cut = provider.RewriteReply(session, 0, "nothing was said");

            // Assert
            Assert.False(cut);
            Assert.Empty(store.Rewrites);
            Assert.Empty(await ProvideAsync(provider, session));
        }

        [Fact]
        public async Task TruncateFrom_AMessageTheSessionDoesNotHold_WithdrawsNothing()
        {
            // Arrange
            (AgentCoreChatHistoryProvider? provider, Fakes.RecordingConversationStore? store, StubSession? session) = await NewConversation();
            AppendTurn(provider, session, turnIndex: 0, "hello", "hi there");

            // Act
            WithdrawnTurns? withdrawn = await provider.TruncateFromAsync(session, "no-such-message", TestContext.Current.CancellationToken);
            await provider.DrainAsync(session);

            // Assert
            Assert.Null(withdrawn);
            Assert.Equal(["hello", "hi there"], (await ProvideAsync(provider, session)).Select(message => message.Text));
            Assert.Equal(["hello", "hi there"], store.Live(ConversationId).Select(row => row.Content.Text));
        }

        /// <summary>
        /// An edit under the summary cuts the store itself, so it waits for every write already queued: a cut that
        /// overtook a pending append would leave the rows it withdrew to land after it.
        /// </summary>
        [Fact]
        public async Task CutUnderSummary_WhileAnAppendIsStillWriting_CutsTheRowsThatAppendWrites()
        {
            // Arrange
            BlockingConversationStore store = new();
            _ = await store.CreateAsync(ConversationId, TestContext.Current.CancellationToken);
            AgentCoreChatHistoryProvider provider = new(store);
            StubSession session = new();
            _ = provider.BeginConversation(session, ConversationId, []);
            AppendTurn(provider, session, turnIndex: 0, "hello", "hi there");
            await provider.DrainAsync(session);
            string parent = (await store.ReadAllAsync(ConversationId, TestContext.Current.CancellationToken))[1].MessageId;
            store.BlockNextAppend();
            AppendTurn(provider, session, turnIndex: 1, "order 41?", "it ships Friday");
            await store.Entered;

            // Act
            ValueTask<WithdrawnTurns?> cut = provider.CutUnderSummaryAsync(session, parent, TestContext.Current.CancellationToken);
            store.Release();
            _ = await cut;
            await provider.DrainAsync(session);

            // Assert
            Assert.Equal(
                ["hello", "hi there"],
                (await store.ReadAllAsync(ConversationId, TestContext.Current.CancellationToken)).Select(row => row.Content.Text));
            Assert.Equal(["hello", "hi there"], (await ProvideAsync(provider, session)).Select(message => message.Text));
        }

        [Fact]
        public async Task InMemoryStore_AfterTurnAndBargeIn_HoldsTheHeardTextInOrder()
        {
            // Arrange
            InMemoryConversationStore store = new();
            _ = await store.CreateAsync(ConversationId, TestContext.Current.CancellationToken);
            AgentCoreChatHistoryProvider provider = new(store);
            StubSession session = new();
            _ = provider.BeginConversation(session, ConversationId, []);
            AppendTurn(provider, session, turnIndex: 0, "order 41?", "it ships Friday from the depot");

            // Act
            _ = provider.RewriteReply(session, 0, "it ships");

            // Assert
            await provider.DrainAsync(session);
            Assert.Equal(
                ["order 41?", "it ships"],
                (await store.ReadAllAsync(ConversationId, TestContext.Current.CancellationToken)).Select(row => row.Content.Text));
        }
    }
}
