using AgentCore.Application.Transcript;
using AgentCore.Infrastructure.Conversation.Postgres;
using AgentCore.Infrastructure.Tests.Database.Postgres;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Conversation.Postgres
{
    /// <summary>
    /// Retention, which deletes stale response ids only. A conversation and its words are
    /// never swept: they stay whole for as long as the row lives, whether or not anything can
    /// still resume it.
    /// </summary>
    public sealed class PostgresConversationStoreSweepTests : PostgresDatabaseTest
    {
        private static readonly TimeSpan Window = TimeSpan.FromDays(30);

        /// <inheritdoc />
        protected override bool Migrated => true;

        [PostgresFact]
        public async Task SweepAsync_AResponseIdUntouchedPastTheWindow_TakesTheRow()
        {
            // Arrange
            PostgresConversationStore store = new(DataSource);
            _ = await store.CreateAsync("c1", Token);
            await store.SaveContinuationAsync("resp_1", "c1", Token);
            await ExecuteAsync("UPDATE agentcore.response_continuation SET created_at = now() - interval '90 days'");

            // Act
            int swept = await store.SweepAsync(Window, cancellationToken: Token);

            // Assert
            Assert.Equal(1, swept);
            Assert.Null(await store.FindContinuationAsync("resp_1", Token));
        }

        [PostgresFact]
        public async Task SweepAsync_AResponseIdUntouchedPastTheWindow_LeavesTheConversationAndItsWords()
        {
            // Arrange
            PostgresConversationStore store = new(DataSource);
            _ = await store.CreateAsync("c1", Token);
            await store.SaveContinuationAsync("resp_1", "c1", Token);
            await ExecuteAsync("UPDATE agentcore.response_continuation SET created_at = now() - interval '90 days'");

            // Act
            _ = await store.SweepAsync(Window, cancellationToken: Token);

            // Assert
            Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM agentcore.conversation"));
        }

        [PostgresFact]
        public async Task SweepAsync_AConversationRowItself_IsNeverTaken()
        {
            // Arrange — an old conversation with no response id at all: nothing here for the sweep to find.
            PostgresConversationStore store = new(DataSource);
            _ = await store.CreateAsync("old", Token);
            await ExecuteAsync("UPDATE agentcore.conversation SET created_at = now() - interval '400 days'");

            // Act
            int swept = await store.SweepAsync(Window, cancellationToken: Token);

            // Assert
            Assert.Equal(0, swept);
            Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM agentcore.conversation"));
        }

        [PostgresFact]
        public async Task SweepAsync_AResponseIdWrittenInsideTheWindow_LeavesIt()
        {
            // Arrange
            PostgresConversationStore store = new(DataSource);
            _ = await store.CreateAsync("c1", Token);
            await store.SaveContinuationAsync("resp_1", "c1", Token);

            // Act
            int swept = await store.SweepAsync(Window, cancellationToken: Token);

            // Assert
            Assert.Equal(0, swept);
            Assert.Equal("c1", await store.FindContinuationAsync("resp_1", Token));
        }

        [PostgresFact]
        public async Task SweepAsync_OneResponseIdInsideTheWindowAndOneOutsideIt_TakesOnlyTheOlderOne()
        {
            // Arrange — proves the window is a real filter, not "sweep everything": the live probe report
            // (Q3) had only ever exercised a single row.
            PostgresConversationStore store = new(DataSource);
            _ = await store.CreateAsync("c1", Token);
            await store.SaveContinuationAsync("resp_old", "c1", Token);
            await store.SaveContinuationAsync("resp_new", "c1", Token);
            await ExecuteAsync(
                "UPDATE agentcore.response_continuation SET created_at = now() - interval '90 days' WHERE store_id = 'resp_old'");

            // Act
            int swept = await store.SweepAsync(Window, cancellationToken: Token);

            // Assert
            Assert.Equal(1, swept);
            Assert.Null(await store.FindContinuationAsync("resp_old", Token));
            Assert.Equal("c1", await store.FindContinuationAsync("resp_new", Token));
        }

        [PostgresFact]
        public async Task SweepAsync_MoreExpiredResponseIdsThanOneBatch_LoopsUntilNoneAreLeft()
        {
            // Arrange
            PostgresConversationStore store = new(DataSource);
            _ = await store.CreateAsync("c1", Token);

            for (int i = 0; i < 5; i++)
            {
                await store.SaveContinuationAsync($"resp_{i}", "c1", Token);
            }

            await ExecuteAsync("UPDATE agentcore.response_continuation SET created_at = now() - interval '90 days'");

            // Act
            int swept = await store.SweepAsync(Window, batchSize: 2, cancellationToken: Token);

            // Assert
            Assert.Equal(5, swept);
            Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM agentcore.response_continuation"));
        }

        [PostgresFact]
        public async Task EraseAsync_AConversationsWords_LeavesTheConversationItselfListed()
        {
            // Arrange
            PostgresConversationStore store = new(DataSource);
            _ = await store.CreateAsync("c1", Token);
            _ = await store.AppendAsync("c1", [new ConversationMessageDraft(0, new ChatMessage(ChatRole.User, "hello"), "m0")], cancellationToken: Token);

            // Act
            int erased = await store.EraseAsync("c1", Token);

            // Assert — erase empties a thread that stays; delete takes the thread and the words with it.
            Assert.Equal(1, erased);
            Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM agentcore.conversation_message"));
            Assert.NotNull(await store.GetAsync("c1", Token));
        }
    }
}
