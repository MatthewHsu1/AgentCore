using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Conversation.Memory
{
    /// <summary>
    /// The store refuses a turn the conversation already saved (owner ruling 2026-09-24, "refuse at save"): two
    /// sessions that resumed the same conversation both run its next turn, and only the first commit is kept.
    /// </summary>
#pragma warning disable CA1859 // store is typed as IConversationStore throughout, as every caller holds it.
    public sealed class InMemoryConversationStoreTurnConflictTests
    {
        private static CancellationToken Token => TestContext.Current.CancellationToken;

        [Fact]
        public async Task AppendAsync_ATurnAlreadySaved_IsRefused_AndWritesNothing()
        {
            // Arrange
            IConversationStore store = new InMemoryConversationStore();
            _ = await store.CreateAsync("c1", Token);
            _ = await CommitAsync(store, "c1", 0, "opener");
            _ = await CommitAsync(store, "c1", 1, "first");

            // Act
            Exception? refused = await Record.ExceptionAsync(() => CommitAsync(store, "c1", 1, "second"));

            // Assert
            _ = Assert.IsType<ConversationTurnConflictException>(refused);
            IReadOnlyList<ConversationMessage> rows = await store.ReadForSessionAsync("c1", Token);
            Assert.Equal(["opener-user", "opener-reply", "first-user", "first-reply"], rows.Select(row => row.MessageId));
            ConversationRecord? record = await store.GetAsync("c1", Token);
            Assert.Equal(("first", 2, 4), (record?.State?.Stage, record?.State?.NextTurnIndex, record?.NextOrdinal));
        }

        [Fact]
        public async Task AppendAsync_TheNextTurnAfterARefusal_AndAnAppendThatNamesNoTurn_BothLand()
        {
            // Arrange
            IConversationStore store = new InMemoryConversationStore();
            _ = await store.CreateAsync("c1", Token);
            _ = await CommitAsync(store, "c1", 0, "opener");
            _ = await Record.ExceptionAsync(() => CommitAsync(store, "c1", 0, "late"));

            // Act
            ConversationMessage outside = await store.AppendMessageAsync("c1", new ChatMessage(ChatRole.User, "outside"), Token);
            IReadOnlyList<ConversationMessage> next = await CommitAsync(store, "c1", 1, "next");

            // Assert
            Assert.Equal((2, 1), (outside.Ordinal, outside.TurnIndex));
            Assert.Equal([(3, 1), (4, 1)], next.Select(row => (row.Ordinal, row.TurnIndex)));
        }

        [Fact]
        public async Task AppendAsync_ManyCommitsOfOneTurnAtOnce_KeepsExactlyOne()
        {
            IConversationStore store = new InMemoryConversationStore();

            for (int iteration = 0; iteration < 50; iteration++)
            {
                // Arrange
                string conversationId = "race-" + iteration;
                int parties = 2 + (iteration % 7);
                _ = await store.CreateAsync(conversationId, Token);
                _ = await CommitAsync(store, conversationId, 0, "opener");
                using Barrier start = new(parties);

                // Act
                Task<bool>[] commits = [.. Enumerable.Range(0, parties).Select(party => Task.Run(
                    async () =>
                    {
                        start.SignalAndWait(Token);
                        try
                        {
                            _ = await CommitAsync(store, conversationId, 1, "writer" + party);
                            return true;
                        }
                        catch (ConversationTurnConflictException)
                        {
                            return false;
                        }
                    },
                    Token))];
                bool[] kept = await Task.WhenAll(commits);

                // Assert
                int winner = Assert.Single(Enumerable.Range(0, parties), party => kept[party]);
                IReadOnlyList<ConversationMessage> rows = await store.ReadForSessionAsync(conversationId, Token);
                Assert.Equal(
                    ["opener-user", "opener-reply", $"writer{winner}-user", $"writer{winner}-reply"],
                    rows.Select(row => row.MessageId));
                ConversationRecord? record = await store.GetAsync(conversationId, Token);
                Assert.Equal("writer" + winner, record?.State?.Stage);
            }
        }

        [Fact]
        public async Task AppendAsync_AStateOlderThanTheStoredOne_WritesTheWordsButNotTheState()
        {
            // Arrange
            IConversationStore store = new InMemoryConversationStore();
            _ = await store.CreateAsync("c1", Token);
            _ = await CommitAsync(store, "c1", 0, "opener");
            _ = await CommitAsync(store, "c1", 1, "first");

            // Act
            IReadOnlyList<ConversationMessage> written = await store.AppendAsync(
                "c1",
                [new ConversationMessageDraft(TurnIndex: null, new ChatMessage(ChatRole.User, "outside"), "outside")],
                new ConversationSessionState { NextTurnIndex = 1, Stage = "stale" },
                Token);

            // Assert
            Assert.Equal("outside", Assert.Single(written).MessageId);
            ConversationRecord? record = await store.GetAsync("c1", Token);
            Assert.Equal(("first", 2), (record?.State?.Stage, record?.State?.NextTurnIndex));
        }

        /// <summary>Commits one turn as a session does: the user's words and the reply, with the state after them.</summary>
        private static async Task<IReadOnlyList<ConversationMessage>> CommitAsync(
            IConversationStore store, string conversationId, int turnIndex, string label)
        {
            return await store.AppendAsync(
                conversationId,
                [
                    new ConversationMessageDraft(turnIndex, new ChatMessage(ChatRole.User, label), label + "-user"),
                    new ConversationMessageDraft(turnIndex, new ChatMessage(ChatRole.Assistant, "re " + label), label + "-reply"),
                ],
                new ConversationSessionState { NextTurnIndex = turnIndex + 1, Stage = label },
                Token);
        }
    }
#pragma warning restore CA1859
}
