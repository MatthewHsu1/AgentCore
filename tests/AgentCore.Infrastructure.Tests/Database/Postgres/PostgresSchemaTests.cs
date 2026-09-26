using AgentCore.Infrastructure.Conversation.Postgres;
using AgentCore.Infrastructure.Database.Postgres;
using Npgsql;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Database.Postgres
{
    /// <summary>
    /// The PostgreSQL schema store 1 and store 3 run on.
    /// </summary>
    public sealed class PostgresSchemaTests : PostgresDatabaseTest
    {
        /// <inheritdoc />
        protected override bool Migrated => false;

        [PostgresTheory]
        [InlineData("agentcore.conversation")]
        [InlineData("agentcore.conversation_principal")]
        [InlineData("agentcore.conversation_message")]
        [InlineData("agentcore.audit_event")]
        [InlineData("agentcore.schema_migration")]
        [InlineData("agentcore.conversation_busy")]
        public async Task ApplyAsync_FreshDatabase_CreatesTheTable(string table)
        {
            // Arrange
            _ = await PostgresSchema.ApplyAsync(DataSource, Token);

            // Act
            bool exists = await ScalarAsync<bool>($"SELECT to_regclass('{table}') IS NOT NULL");

            // Assert
            Assert.True(exists);
        }

        [PostgresFact]
        public async Task ApplyAsync_FreshDatabase_GivesConversationANextOrdinalColumn()
        {
            // Arrange
            _ = await PostgresSchema.ApplyAsync(DataSource, Token);

            // Act
            string column = await ScalarAsync<string>(
                """
            SELECT is_nullable || ',' || column_default
              FROM information_schema.columns
             WHERE table_schema = 'agentcore' AND table_name = 'conversation' AND column_name = 'next_ordinal'
            """);

            // Assert
            Assert.Equal("NO,0", column);
        }

        [PostgresFact]
        public async Task ApplyAsync_FreshDatabase_AppliesEveryMigration()
        {
            // Arrange, Act
            IReadOnlyList<string> applied = await PostgresSchema.ApplyAsync(DataSource, Token);

            // Assert
            Assert.Equal(PostgresSchema.Versions, applied);
        }

        [PostgresFact]
        public async Task ApplyAsync_AlreadyCurrent_AppliesNothing()
        {
            // Arrange
            _ = await PostgresSchema.ApplyAsync(DataSource, Token);

            // Act
            IReadOnlyList<string> second = await PostgresSchema.ApplyAsync(DataSource, Token);

            // Assert
            Assert.Empty(second);
        }

        [PostgresFact]
        public async Task ApplyAsync_AsTheWriterRoleOnACurrentDatabase_AppliesNothing()
        {
            // Arrange — this is what the host does on every start. The running system logs in as a member
            // of agentcore_writer, which may create nothing, so a start that tried to migrate would fail
            // on an ordinary boot rather than only on a fresh database.
            _ = await PostgresSchema.ApplyAsync(DataSource, Token);
            await using NpgsqlDataSource asWriter = await OpenAsWriterAsync();

            // Act
            IReadOnlyList<string> applied = await PostgresSchema.ApplyAsync(asWriter, Token);

            // Assert
            Assert.Empty(applied);
        }

        [PostgresTheory]
        [InlineData("UPDATE", false)]
        [InlineData("DELETE", false)]
        [InlineData("TRUNCATE", false)]
        [InlineData("INSERT", true)]
        [InlineData("SELECT", true)]
        public async Task ApplyAsync_FreshDatabase_LeavesTheWriterInsertAndSelectOnly(string privilege, bool expected)
        {
            // Arrange
            _ = await PostgresSchema.ApplyAsync(DataSource, Token);

            // Act
            bool held = await ScalarAsync<bool>($"SELECT has_table_privilege('agentcore_writer', 'agentcore.audit_event', '{privilege}')");

            // Assert
            Assert.Equal(expected, held);
        }

        [PostgresTheory]
        [InlineData("UPDATE")]
        [InlineData("DELETE")]
        [InlineData("TRUNCATE")]
        public async Task ApplyAsync_PublicHoldsGrantAll_StillLeavesTheWriterWithoutThePrivilege(string privilege)
        {
            // Arrange — the hole the PUBLIC revoke exists to close. An earlier migration left PUBLIC
            // holding everything on new tables, and revoking from the role alone does not take it back.
            await ExecuteAsync("ALTER DEFAULT PRIVILEGES GRANT ALL ON TABLES TO PUBLIC");
            _ = await PostgresSchema.ApplyAsync(DataSource, Token);

            // Act
            bool held = await ScalarAsync<bool>($"SELECT has_table_privilege('agentcore_writer', 'agentcore.audit_event', '{privilege}')");

            // Assert
            Assert.False(held);
        }

        [PostgresTheory]
        [InlineData("UPDATE agentcore.audit_event SET kind = 'tampered'")]
        [InlineData("DELETE FROM agentcore.audit_event")]
        [InlineData("TRUNCATE agentcore.audit_event")]
        public async Task AuditEvent_OwnerWritesOverAnExistingRow_IsRefusedByTrigger(string statement)
        {
            // Arrange
            _ = await PostgresSchema.ApplyAsync(DataSource, Token);
            await InsertOneEventAsync();

            // Act
            Exception? refusal = await Record.ExceptionAsync(() => ExecuteAsync(statement));

            // Assert
            Assert.Contains("append-only", Assert.IsType<PostgresException>(refusal).MessageText, StringComparison.Ordinal);
        }

        [PostgresFact]
        public async Task AuditEvent_SuppliedWritePosition_IsRefused()
        {
            // Arrange — the database allocates write_position, so pin that it refuses a supplied one.
            _ = await PostgresSchema.ApplyAsync(DataSource, Token);

            // Act
            Exception? refusal = await Record.ExceptionAsync(() => ExecuteAsync(
                """
            INSERT INTO agentcore.audit_event (write_position, conversation_id, event_id, sequence, kind, occurred_at)
            VALUES (1, 'C1', gen_random_uuid(), 1, 'conversation.started', now())
            """));

            // Assert
            Assert.Equal("428C9", Assert.IsType<PostgresException>(refusal).SqlState);
        }

        [PostgresFact]
        public async Task AuditEvent_SecondRowOnTheSameConversationAndSequence_IsRefused()
        {
            // Arrange — raw SQL stands in for the store here, so the table itself is what catches two
            // rows landing on the same conversation and sequence.
            _ = await PostgresSchema.ApplyAsync(DataSource, Token);
            await InsertOneEventAsync();

            // Act
            Exception? refusal = await Record.ExceptionAsync(() => ExecuteAsync(
                """
            INSERT INTO agentcore.audit_event (conversation_id, event_id, sequence, kind, occurred_at)
            VALUES ('C1', gen_random_uuid(), 1, 'conversation.ended', now())
            """));

            // Assert
            Assert.Equal("23505", Assert.IsType<PostgresException>(refusal).SqlState);
        }

        [PostgresFact]
        public async Task AuditEvent_SessionReplicationRoleIsReplica_BypassesEveryTrigger()
        {
            // Arrange — session_replication_role bypasses all three triggers in one statement with no
            // DDL. Nothing detects that edit; see the fourth design amendment in docs/BUILD.md.
            _ = await PostgresSchema.ApplyAsync(DataSource, Token);
            await ExecuteAsync(
                """
            INSERT INTO agentcore.audit_event (conversation_id, event_id, sequence, kind, occurred_at)
            VALUES ('C1', gen_random_uuid(), 0, 'conversation.started', now())
            """);

            // Act
            await ExecuteAsync(
                """
            SET session_replication_role = replica;
            UPDATE agentcore.audit_event SET kind = 'conversation.ended' WHERE sequence = 0;
            SET session_replication_role = origin;
            """);

            // Assert
            Assert.Equal("conversation.ended", await ScalarAsync<string>("SELECT kind FROM agentcore.audit_event WHERE sequence = 0"));
        }

        [PostgresFact]
        public async Task ConversationMessage_SecondMessageOnTheSameOrdinal_IsRefused()
        {
            // Arrange
            _ = await PostgresSchema.ApplyAsync(DataSource, Token);
            await ExecuteAsync("INSERT INTO agentcore.conversation (conversation_id) VALUES ('C1')");
            await ExecuteAsync(
                "INSERT INTO agentcore.conversation_message (conversation_id, ordinal, turn_index, role, content, message_id) VALUES ('C1', 0, 0, 'user', '{}', 'm0')");

            // Act
            Exception? refusal = await Record.ExceptionAsync(() => ExecuteAsync(
                "INSERT INTO agentcore.conversation_message (conversation_id, ordinal, turn_index, role, content, message_id) VALUES ('C1', 0, 0, 'assistant', '{}', 'm1')"));

            // Assert
            Assert.Equal("23505", Assert.IsType<PostgresException>(refusal).SqlState);
        }

        [PostgresFact]
        public async Task Versions_AreTheMigrationsThisAssemblyCarries_InOrder()
        {
            Assert.Equal(["001_agentcore", "002_conversation_busy"], PostgresSchema.Versions);
        }

        [PostgresFact]
        public async Task ConversationBusy_TheWriterRole_MarksRenewsAndClears()
        {
            // Arrange
            _ = await PostgresSchema.ApplyAsync(DataSource, Token);
            await using NpgsqlDataSource asWriter = await OpenAsWriterAsync();
            PostgresConversationStore store = new(asWriter);

            // Act
            bool marked = await store.TryMarkBusyAsync("c1", "host-a", TimeSpan.FromMinutes(1), Token);
            bool renewed = await store.TryMarkBusyAsync("c1", "host-a", TimeSpan.FromMinutes(1), Token);
            await store.ClearBusyAsync("c1", "host-a", Token);

            // Assert
            Assert.Equal((true, true), (marked, renewed));
            Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM agentcore.conversation_busy"));
        }

        [PostgresFact]
        public async Task ConversationBusy_IsUnlogged_SoAMarkNeverWaitsForAWalFlush()
        {
            // Arrange
            _ = await PostgresSchema.ApplyAsync(DataSource, Token);

            // Act
            string persistence = await ScalarAsync<string>(
                "SELECT relpersistence::text FROM pg_class WHERE oid = 'agentcore.conversation_busy'::regclass");

            // Assert
            Assert.Equal("u", persistence);
        }

        [PostgresFact]
        public async Task ConversationMessage_WithNoConversationRow_IsRefused()
        {
            // Arrange
            _ = await PostgresSchema.ApplyAsync(DataSource, Token);
            await ExecuteAsync("INSERT INTO agentcore.conversation (conversation_id) VALUES ('present')");

            // Act
            Exception? refusal = await Record.ExceptionAsync(() => ExecuteAsync(
                "INSERT INTO agentcore.conversation_message (conversation_id, ordinal, turn_index, role, content, message_id) VALUES ('absent', 0, 0, 'user', '{}', 'm0')"));

            // Assert
            Assert.Equal("23503", Assert.IsType<PostgresException>(refusal).SqlState);
        }

        /// <summary>
        /// The cascade is the whole reason one store holds both. It reaches conversation_message and
        /// conversation_principal, and it deliberately does not reach audit_event: a trigger refuses every
        /// DELETE there, and the trail outlives the conversation on purpose.
        /// </summary>
        [PostgresFact]
        public async Task DeletingAConversation_TakesItsMessagesAndPrincipals_AndLeavesItsAuditEvents()
        {
            // Arrange
            _ = await PostgresSchema.ApplyAsync(DataSource, Token);
            await ExecuteAsync("INSERT INTO agentcore.conversation (conversation_id) VALUES ('C1')");
            await ExecuteAsync(
                "INSERT INTO agentcore.conversation_message (conversation_id, ordinal, turn_index, role, content, message_id) VALUES ('C1', 0, 0, 'user', '{}', 'm0')");
            await ExecuteAsync(
                "INSERT INTO agentcore.conversation_principal (conversation_id, principal_key, role) VALUES ('C1', 'p1', 'owner')");
            await InsertOneEventAsync();

            // Act
            await ExecuteAsync("DELETE FROM agentcore.conversation WHERE conversation_id = 'C1'");

            // Assert
            Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM agentcore.conversation_message"));
            Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM agentcore.conversation_principal"));
            Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM agentcore.audit_event"));
        }

        private Task InsertOneEventAsync()
        {
            return ExecuteAsync(
            """
        INSERT INTO agentcore.audit_event (conversation_id, event_id, sequence, kind, occurred_at)
        VALUES ('C1', gen_random_uuid(), 1, 'conversation.started', now())
        """);
        }
    }
}
