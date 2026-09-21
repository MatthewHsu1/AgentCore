using AgentCore.Application.Transcript;
using AgentCore.Infrastructure.Conversation.Postgres;
using AgentCore.Infrastructure.Tests.Database.Postgres;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Conversation.Postgres
{
    /// <summary>
    /// Retention, which deletes the conversation and lets the cascade take the words.
    /// </summary>
    public sealed class PostgresConversationStoreSweepTests : PostgresDatabaseTest
    {
        private static readonly TimeSpan Window = TimeSpan.FromDays(30);

        /// <inheritdoc />
        protected override bool Migrated => true;

        private static ConversationMessageDraft Word()
        {
            return new(0, new ChatMessage(ChatRole.User, "hello"), "m0");
        }

        [PostgresFact]
        public async Task SweepAsync_AConversationWhoseLastMessageIsOld_TakesTheConversationAndItsWords()
        {
            // Arrange
            PostgresConversationStore store = new(DataSource);
            _ = await store.CreateAsync("old", Token);
            _ = await store.AppendAsync("old", [Word()], cancellationToken: Token);
            await ExecuteAsync("UPDATE agentcore.conversation_message SET updated_at = now() - interval '90 days'");

            // Act
            int swept = await store.SweepAsync(Window, cancellationToken: Token);

            // Assert
            Assert.Equal(1, swept);
            Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM agentcore.conversation"));
            Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM agentcore.conversation_message"));
        }

        [PostgresFact]
        public async Task SweepAsync_AConversationWithNoWordsAtAll_TakesItOnCreatedAt()
        {
            // Arrange
            PostgresConversationStore store = new(DataSource);
            _ = await store.CreateAsync("empty", Token);
            await ExecuteAsync("UPDATE agentcore.conversation SET created_at = now() - interval '90 days'");

            // Act
            int swept = await store.SweepAsync(Window, cancellationToken: Token);

            // Assert
            Assert.Equal(1, swept);
            Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM agentcore.conversation"));
        }

        /// <summary>
        /// created_at is old and the message is fresh, so the message time must win. This is the whole
        /// point of coalescing rather than reading either column alone.
        /// </summary>
        [PostgresFact]
        public async Task SweepAsync_AConversationSpokenOnInsideTheWindow_LeavesIt()
        {
            // Arrange
            PostgresConversationStore store = new(DataSource);
            _ = await store.CreateAsync("fresh", Token);
            _ = await store.AppendAsync("fresh", [Word()], cancellationToken: Token);
            await ExecuteAsync("UPDATE agentcore.conversation SET created_at = now() - interval '90 days'");

            // Act
            int swept = await store.SweepAsync(Window, cancellationToken: Token);

            // Assert
            Assert.Equal(0, swept);
            Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM agentcore.conversation"));
        }

        [PostgresFact]
        public async Task SweepAsync_ASweptConversation_LeavesItsAuditEvents()
        {
            // Arrange
            PostgresConversationStore store = new(DataSource);
            _ = await store.CreateAsync("old", Token);
            await ExecuteAsync("UPDATE agentcore.conversation SET created_at = now() - interval '90 days'");
            await ExecuteAsync(
                """
            INSERT INTO agentcore.audit_event (conversation_id, event_id, sequence, kind, occurred_at)
            VALUES ('old', gen_random_uuid(), 0, 'conversation.started', now())
            """);

            // Act
            _ = await store.SweepAsync(Window, cancellationToken: Token);

            // Assert
            Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM agentcore.audit_event"));
        }

        [PostgresFact]
        public async Task SweepAsync_MoreExpiredConversationsThanOneBatch_LoopsUntilNoneAreLeft()
        {
            // Arrange
            PostgresConversationStore store = new(DataSource);
            for (int i = 0; i < 5; i++)
            {
                _ = await store.CreateAsync($"c{i}", Token);
            }

            await ExecuteAsync("UPDATE agentcore.conversation SET created_at = now() - interval '90 days'");

            // Act
            int swept = await store.SweepAsync(Window, batchSize: 2, cancellationToken: Token);

            // Assert
            Assert.Equal(5, swept);
            Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM agentcore.conversation"));
        }

        [PostgresFact]
        public async Task EraseAsync_AConversationsWords_LeavesTheConversationItselfListed()
        {
            // Arrange
            PostgresConversationStore store = new(DataSource);
            _ = await store.CreateAsync("c1", Token);
            _ = await store.AppendAsync("c1", [Word()], cancellationToken: Token);

            // Act
            int erased = await store.EraseAsync("c1", Token);

            // Assert — erase empties a thread that stays; delete takes the thread and the words with it.
            Assert.Equal(1, erased);
            Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM agentcore.conversation_message"));
            Assert.NotNull(await store.GetAsync("c1", Token));
        }
    }
}
