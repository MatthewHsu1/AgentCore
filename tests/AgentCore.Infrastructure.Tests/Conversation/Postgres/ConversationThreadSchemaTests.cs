using AgentCore.Infrastructure.Tests.Database.Postgres;
using Npgsql;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Conversation.Postgres
{
    /// <summary>Store 0's shape, and what the running role may do to it.</summary>
    /// <remarks>
    /// <c>conversation</c> and <c>role</c> are both non-reserved keywords in PostgreSQL and need no quoting. That
    /// is what the first test proves: the migration would fail to apply at all if it were untrue.
    /// <para>
    /// The primary key's column order is read from the catalogue rather than inferred from behaviour. A
    /// listing filters by principal, so principal has to lead, and either order satisfies uniqueness.
    /// </para>
    /// </remarks>
    public sealed class ConversationThreadSchemaTests : PostgresDatabaseTest
    {
        /// <inheritdoc />
        protected override bool Migrated => true;

        [PostgresFact]
        public async Task Migration_Applied_MakesBothTables()
        {
            // Act
            long tables = await ScalarAsync<long>(
                """
            SELECT count(*) FROM information_schema.tables
             WHERE table_schema = 'agentcore' AND table_name IN ('conversation', 'conversation_principal')
            """);

            // Assert
            Assert.Equal(2, tables);
        }

        [PostgresFact]
        public async Task Conversation_AStatusOutsideTheTwo_IsRefused()
        {
            // Act
            Exception? refusal = await Record.ExceptionAsync(
                () => ExecuteAsync("INSERT INTO agentcore.conversation (conversation_id, status) VALUES ('c1', 'nonsense')"));

            // Assert
            Assert.Equal("23514", Assert.IsType<PostgresException>(refusal).SqlState);
        }

        [PostgresFact]
        public async Task ConversationPrincipal_ItsConversationDeleted_GoesWithIt()
        {
            // Arrange
            await ExecuteAsync("INSERT INTO agentcore.conversation (conversation_id) VALUES ('c1')");
            await ExecuteAsync("INSERT INTO agentcore.conversation_principal (conversation_id, principal_key, role) VALUES ('c1', 'p1', 'caller')");

            // Act
            await ExecuteAsync("DELETE FROM agentcore.conversation WHERE conversation_id = 'c1'");

            // Assert
            Assert.Equal(0, await ScalarAsync<long>("SELECT count(*) FROM agentcore.conversation_principal"));
        }

        [PostgresFact]
        public async Task ConversationPrincipal_TheSamePairTwice_IsRefusedByThePrimaryKey()
        {
            // Arrange
            await ExecuteAsync("INSERT INTO agentcore.conversation (conversation_id) VALUES ('c1')");
            await ExecuteAsync("INSERT INTO agentcore.conversation_principal (conversation_id, principal_key, role) VALUES ('c1', 'p1', 'caller')");

            // Act
            Exception? refusal = await Record.ExceptionAsync(
                () => ExecuteAsync("INSERT INTO agentcore.conversation_principal (conversation_id, principal_key, role) VALUES ('c1', 'p1', 'agent')"));

            // Assert
            Assert.Equal("23505", Assert.IsType<PostgresException>(refusal).SqlState);
        }

        [PostgresFact]
        public async Task ConversationPrincipal_ItsPrimaryKey_LeadsWithThePrincipal()
        {
            // Act
            string columns = await ScalarAsync<string>(
                """
            SELECT string_agg(a.attname, ',' ORDER BY k.ord)
              FROM pg_index i
              JOIN LATERAL unnest(i.indkey) WITH ORDINALITY AS k(attnum, ord) ON true
              JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum
             WHERE i.indrelid = 'agentcore.conversation_principal'::regclass AND i.indisprimary
            """);

            // Assert
            Assert.Equal("principal_key,conversation_id", columns);
        }

        [PostgresFact]
        public async Task ConversationPrincipal_TheOtherDirection_HasItsOwnIndex()
        {
            // Act
            long indexes = await ScalarAsync<long>(
                """
            SELECT count(*) FROM pg_indexes
             WHERE schemaname = 'agentcore'
               AND tablename = 'conversation_principal'
               AND indexname = 'conversation_principal_conversation_idx'
            """);

            // Assert
            Assert.Equal(1, indexes);
        }

        [PostgresTheory]
        [InlineData("agentcore.conversation", "SELECT")]
        [InlineData("agentcore.conversation", "INSERT")]
        [InlineData("agentcore.conversation", "UPDATE")]
        [InlineData("agentcore.conversation", "DELETE")]
        [InlineData("agentcore.conversation_principal", "SELECT")]
        [InlineData("agentcore.conversation_principal", "INSERT")]
        [InlineData("agentcore.conversation_principal", "UPDATE")]
        [InlineData("agentcore.conversation_principal", "DELETE")]
        public async Task Writer_EachStoreZeroPrivilege_IsGranted(string table, string privilege)
        {
            // Act
            bool granted = await ScalarAsync<bool>(
                $"SELECT has_table_privilege('agentcore_writer', '{table}', '{privilege}')");

            // Assert
            Assert.True(granted);
        }
    }
}
