using AgentCore.Application.Conversation;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using AgentCore.Infrastructure.Conversation.Postgres;
using AgentCore.Infrastructure.Tests.Database.Postgres;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Conversation.Postgres
{
    /// <summary>
    /// The store refuses a turn the conversation already saved, in PostgreSQL (owner ruling 2026-09-24, "refuse at
    /// save"): two requests, on one machine or two, that resumed the same conversation both run its next turn, and
    /// exactly one commit lands.
    /// </summary>
#pragma warning disable CA1859 // store is typed as IConversationStore throughout, as every caller holds it.
    public sealed class PostgresConversationStoreTurnConflictTests : PostgresDatabaseTest
    {
        /// <inheritdoc />
        protected override bool Migrated => true;

        [PostgresFact]
        public async Task AppendAsync_ATurnAlreadySaved_IsRefused_AndWritesNeitherRowsNorState()
        {
            // Arrange
            IConversationStore store = new PostgresConversationStore(DataSource);
            _ = await store.CreateAsync("c1", Token);
            _ = await CommitAsync(store, "c1", 0, "opener");
            _ = await CommitAsync(store, "c1", 1, "first");

            // Act
            Exception? refused = await Record.ExceptionAsync(() => CommitAsync(store, "c1", 1, "second"));

            // Assert
            _ = Assert.IsType<ConversationTurnConflictException>(refused);
            IReadOnlyList<ConversationMessage> rows = await store.ReadAllAsync("c1", Token);
            Assert.Equal(["opener-user", "opener-reply", "first-user", "first-reply"], rows.Select(row => row.MessageId));
            ConversationRecord? record = await store.GetAsync("c1", Token);
            Assert.Equal(("first", 2, 4), (record?.State?.Stage, record?.State?.NextTurnIndex, record?.NextOrdinal));
        }

        [PostgresFact]
        public async Task AppendAsync_TheNextTurnAfterARefusal_AndAnAppendThatNamesNoTurn_BothLand()
        {
            // Arrange
            IConversationStore store = new PostgresConversationStore(DataSource);
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

        [PostgresFact]
        public async Task AppendAsync_ManyCommitsOfOneTurnAtOnce_KeepsExactlyOne()
        {
            // Every writer runs on its own pooled connection, so each is its own transaction on the server: the
            // losers block on the conversation row until the winner commits, then re-check the turn against it.
            IConversationStore store = new PostgresConversationStore(DataSource);

            for (int iteration = 0; iteration < 50; iteration++)
            {
                // Arrange
                string conversationId = "race-" + iteration;
                int parties = 2 + (iteration % 7);
                _ = await store.CreateAsync(conversationId, Token);
                _ = await CommitAsync(store, conversationId, 0, "opener");
                TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);

                // Act
                Task<bool>[] commits = [.. Enumerable.Range(0, parties).Select(async party =>
                {
                    await start.Task;
                    try
                    {
                        _ = await CommitAsync(store, conversationId, 1, "writer" + party);
                        return true;
                    }
                    catch (ConversationTurnConflictException)
                    {
                        return false;
                    }
                })];
                start.SetResult();
                bool[] kept = await Task.WhenAll(commits);

                // Assert
                int winner = Assert.Single(Enumerable.Range(0, parties), party => kept[party]);
                IReadOnlyList<ConversationMessage> rows = await store.ReadAllAsync(conversationId, Token);
                Assert.Equal(
                    ["opener-user", "opener-reply", $"writer{winner}-user", $"writer{winner}-reply"],
                    rows.Select(row => row.MessageId));
                ConversationRecord? record = await store.GetAsync(conversationId, Token);
                Assert.Equal(("writer" + winner, 2), (record?.State?.Stage, record?.State?.NextTurnIndex));
            }
        }

        [PostgresFact]
        public async Task AppendAsync_AStateOlderThanTheStoredOne_WritesTheWordsButNotTheState()
        {
            // Arrange
            IConversationStore store = new PostgresConversationStore(DataSource);
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
