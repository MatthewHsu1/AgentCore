using AgentCore.Infrastructure.Database.Postgres;
using Npgsql;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Database.Postgres
{
    /// <summary>
    /// A database an earlier release migrated, met by a host of this one. The host applies what is pending as it
    /// starts, on its own connection; a host that connects as a member of <c>agentcore_writer</c> may create nothing.
    /// </summary>
    public sealed class PostgresSchemaUpgradeTests : PostgresDatabaseTest
    {
        /// <inheritdoc />
        protected override bool Migrated => false;

        [PostgresFact]
        public async Task ApplyAsync_ADatabaseMigratedTo001_AppliesOnly002()
        {
            // Arrange: the state a database left by the 001-only release is in.
            _ = await PostgresSchema.ApplyAsync(DataSource, Token);
            await ExecuteAsync("DROP TABLE agentcore.conversation_busy");
            await ExecuteAsync("DELETE FROM agentcore.schema_migration WHERE version = '002_conversation_busy'");
            await ExecuteAsync("INSERT INTO agentcore.conversation (conversation_id) VALUES ('kept')");

            // Act
            IReadOnlyList<string> applied = await PostgresSchema.ApplyAsync(DataSource, Token);

            // Assert
            Assert.Equal(["002_conversation_busy"], applied);
            Assert.True(await ScalarAsync<bool>("SELECT to_regclass('agentcore.conversation_busy') IS NOT NULL"));
            Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM agentcore.conversation"));
        }

        [PostgresFact]
        public async Task ApplyAsync_AsTheWriterRoleOnADatabaseMigratedTo001_RefusesAndNamesTheMigrationAndWhoAppliesIt()
        {
            // Arrange
            await MigrateTo001Async();
            await using NpgsqlDataSource asWriter = await OpenAsWriterAsync();

            // Act
            Exception? refusal = await Record.ExceptionAsync(() => PostgresSchema.ApplyAsync(asWriter, Token));

            // Assert
            InvalidOperationException failure = Assert.IsType<InvalidOperationException>(refusal);
            Assert.Contains("002_conversation_busy", failure.Message, StringComparison.Ordinal);
            Assert.Contains("the role that applied 001_agentcore", failure.Message, StringComparison.Ordinal);
            Assert.Contains("PostgresSchema.ApplyAsync", failure.Message, StringComparison.Ordinal);
            Assert.Equal("42501", Assert.IsType<PostgresException>(failure.InnerException).SqlState);
            Assert.False(await ScalarAsync<bool>("SELECT to_regclass('agentcore.conversation_busy') IS NOT NULL"));
        }

        [PostgresFact]
        public async Task ApplyAsync_AsTheWriterRoleOnAnEmptyDatabase_AsksForARoleThatMayCreateTheSchema()
        {
            // Arrange
            await ExecuteAsync(
                "DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'agentcore_writer') "
                + "THEN CREATE ROLE agentcore_writer NOLOGIN; END IF; END $$");
            await using NpgsqlDataSource asWriter = await OpenAsWriterAsync();

            // Act
            Exception? refusal = await Record.ExceptionAsync(() => PostgresSchema.ApplyAsync(asWriter, Token));

            // Assert
            InvalidOperationException failure = Assert.IsType<InvalidOperationException>(refusal);
            Assert.Contains("001_agentcore, 002_conversation_busy", failure.Message, StringComparison.Ordinal);
            Assert.Contains("may create the 'agentcore' schema", failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("the role that applied", failure.Message, StringComparison.Ordinal);
            Assert.Equal("42501", Assert.IsType<PostgresException>(failure.InnerException).SqlState);
            Assert.False(await ScalarAsync<bool>("SELECT to_regnamespace('agentcore') IS NOT NULL"));
        }

        [PostgresFact]
        public async Task ApplyAsync_AsTheWriterRoleAfterTheOwnerApplied002_Starts()
        {
            // Arrange
            await MigrateTo001Async();
            await using NpgsqlDataSource asWriter = await OpenAsWriterAsync();
            _ = await Record.ExceptionAsync(() => PostgresSchema.ApplyAsync(asWriter, Token));
            _ = await PostgresSchema.ApplyAsync(DataSource, Token);

            // Act
            IReadOnlyList<string> applied = await PostgresSchema.ApplyAsync(asWriter, Token);

            // Assert
            Assert.Empty(applied);
            Assert.True(await ScalarAsync<bool>("SELECT to_regclass('agentcore.conversation_busy') IS NOT NULL"));
        }

        [PostgresFact]
        public async Task ApplyAsync_003_DeletesThePointerRow_AndLeavesTheEnvelopeRowAsAPlainRow()
        {
            // Arrange: the response_continuation shape a 002-only release left, with one pointer row
            // (store_id names its own conversation, the old same-id resume path) and one real per-turn
            // envelope row.
            await MigrateTo002Async();
            await ExecuteAsync("INSERT INTO agentcore.conversation (conversation_id) VALUES ('conv_1')");
            await ExecuteAsync(
                """
                INSERT INTO agentcore.response_continuation (store_id, conversation_id, envelope, created_at, updated_at)
                VALUES ('conv_1', 'conv_1', '{"$continuationPointer":"resp_1"}'::jsonb, '2020-01-01T00:00:00+00', now())
                """);
            await ExecuteAsync(
                """
                INSERT INTO agentcore.response_continuation (store_id, conversation_id, envelope, created_at, updated_at)
                VALUES ('resp_1', 'conv_1', '{"conversationId":"conv_1","state":{}}'::jsonb, '2020-01-01T00:00:00+00', now())
                """);

            // Act
            IReadOnlyList<string> applied = await PostgresSchema.ApplyAsync(DataSource, Token);

            // Assert
            Assert.Equal(["003_response_continuation"], applied);
            Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM agentcore.response_continuation WHERE store_id = 'conv_1'"));
            Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM agentcore.response_continuation WHERE store_id = 'resp_1'"));
            Assert.Equal(
                new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                await ScalarAsync<DateTime>("SELECT created_at FROM agentcore.response_continuation WHERE store_id = 'resp_1'"));
            Assert.Equal(
                0L,
                await ScalarAsync<long>(
                    """
                    SELECT count(*) FROM information_schema.columns
                     WHERE table_schema = 'agentcore' AND table_name = 'response_continuation'
                       AND column_name IN ('envelope', 'updated_at')
                    """));
        }

        /// <summary>Leaves the database as the 001-only release did.</summary>
        private async Task MigrateTo001Async()
        {
            _ = await PostgresSchema.ApplyAsync(DataSource, Token);
            await ExecuteAsync("DROP TABLE agentcore.conversation_busy");
            await ExecuteAsync("DELETE FROM agentcore.schema_migration WHERE version = '002_conversation_busy'");
        }

        /// <summary>Leaves the database as a 002-only release did: response_continuation still holds an envelope.</summary>
        private async Task MigrateTo002Async()
        {
            _ = await PostgresSchema.ApplyAsync(DataSource, Token);
            await ExecuteAsync("DELETE FROM agentcore.schema_migration WHERE version = '003_response_continuation'");
            await ExecuteAsync("DROP INDEX agentcore.response_continuation_retention_idx");
            await ExecuteAsync(
                """
                ALTER TABLE agentcore.response_continuation
                    ADD COLUMN envelope   jsonb       NOT NULL,
                    ADD COLUMN updated_at timestamptz NOT NULL DEFAULT now()
                """);
        }
    }
}
