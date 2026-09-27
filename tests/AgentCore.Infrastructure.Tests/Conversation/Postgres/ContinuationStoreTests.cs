using AgentCore.Infrastructure.Conversation.Postgres;
using AgentCore.Infrastructure.Tests.Database.Postgres;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Conversation.Postgres
{
    /// <summary>The continuation rows beside the conversations: one row per response id.</summary>
    public sealed class ContinuationStoreTests : PostgresDatabaseTest
    {
        /// <inheritdoc />
        protected override bool Migrated => true;

        [PostgresFact]
        public async Task Migration_Applied_MakesTheContinuationTable()
        {
            // Act
            long tables = await ScalarAsync<long>(
                """
            SELECT count(*) FROM information_schema.tables
             WHERE table_schema = 'agentcore' AND table_name = 'response_continuation'
            """);

            // Assert
            Assert.Equal(1, tables);
        }

        [PostgresFact]
        public async Task RoundTrip_SaveFindDelete()
        {
            // Arrange
            PostgresConversationStore store = new(DataSource);
            _ = await store.CreateAsync("conv_1", Token);

            // Act
            await store.SaveContinuationAsync("resp_1", "conv_1", Token);
            string? found = await store.FindContinuationAsync("resp_1", Token);

            // Assert
            Assert.Equal("conv_1", found);

            // Act
            await store.DeleteContinuationAsync("resp_1", Token);

            // Assert
            Assert.Null(await store.FindContinuationAsync("resp_1", Token));
        }

        [PostgresFact]
        public async Task SaveContinuation_TheSameResponseIdTwice_KeepsTheFirstConversation()
        {
            // Arrange — a response id is written once, in its turn's own commit, and never rewritten.
            PostgresConversationStore store = new(DataSource);
            _ = await store.CreateAsync("conv_1", Token);
            _ = await store.CreateAsync("conv_2", Token);
            await store.SaveContinuationAsync("resp_1", "conv_1", Token);

            // Act
            await store.SaveContinuationAsync("resp_1", "conv_2", Token);

            // Assert
            Assert.Equal("conv_1", await store.FindContinuationAsync("resp_1", Token));
        }

        [PostgresFact]
        public async Task FindContinuation_UnknownId_AnswersNull()
        {
            // Arrange
            PostgresConversationStore store = new(DataSource);

            // Act
            string? found = await store.FindContinuationAsync("resp_missing", Token);

            // Assert
            Assert.Null(found);
        }
    }
}
