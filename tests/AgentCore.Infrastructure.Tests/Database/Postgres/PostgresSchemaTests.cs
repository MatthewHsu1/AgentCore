using AgentCore.Infrastructure.Database.Postgres;
using Npgsql;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Database.Postgres;

/// <summary>
/// The PostgreSQL schema store 1 and store 3 run on.
/// </summary>
/// <remarks>
/// These need a live PostgreSQL and skip without one — <see cref="PostgresFactAttribute"/> names the
/// variable. Each test gets a database of its own and drops it again, because a migration is not a
/// thing two tests can share.
/// </remarks>
public sealed class PostgresSchemaTests : PostgresDatabaseTest
{
    /// <inheritdoc />
    protected override bool Migrated => false;

    [PostgresTheory]
    [InlineData("call")]
    [InlineData("call_principal")]
    [InlineData("call_message")]
    [InlineData("audit_event")]
    public async Task ApplyAsync_FreshDatabase_CreatesTheTable(string table)
    {
        // Arrange
        await PostgresSchema.ApplyAsync(DataSource, Token);

        // Act
        var exists = await ScalarAsync<bool>($"SELECT to_regclass('{table}') IS NOT NULL");

        // Assert
        Assert.True(exists);
    }

    [PostgresFact]
    public async Task ApplyAsync_FreshDatabase_AppliesEveryMigration()
    {
        // Arrange, Act
        var applied = await PostgresSchema.ApplyAsync(DataSource, Token);

        // Assert
        Assert.Equal(PostgresSchema.Versions, applied);
    }

    [PostgresFact]
    public async Task ApplyAsync_AlreadyCurrent_AppliesNothing()
    {
        // Arrange
        await PostgresSchema.ApplyAsync(DataSource, Token);

        // Act
        var second = await PostgresSchema.ApplyAsync(DataSource, Token);

        // Assert
        Assert.Empty(second);
    }

    [PostgresFact]
    public async Task ApplyAsync_AsTheWriterRoleOnACurrentDatabase_AppliesNothing()
    {
        // Arrange — this is what the host does on every start. The running system logs in as a member
        // of agentcore_writer, which may create nothing, so a start that tried to migrate would fail
        // on an ordinary boot rather than only on a fresh database.
        await PostgresSchema.ApplyAsync(DataSource, Token);
        await using var asWriter = await OpenAsWriterAsync();

        // Act
        var applied = await PostgresSchema.ApplyAsync(asWriter, Token);

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
        await PostgresSchema.ApplyAsync(DataSource, Token);

        // Act
        var held = await ScalarAsync<bool>($"SELECT has_table_privilege('agentcore_writer', 'audit_event', '{privilege}')");

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
        await PostgresSchema.ApplyAsync(DataSource, Token);

        // Act
        var held = await ScalarAsync<bool>($"SELECT has_table_privilege('agentcore_writer', 'audit_event', '{privilege}')");

        // Assert
        Assert.False(held);
    }

    [PostgresTheory]
    [InlineData("UPDATE audit_event SET kind = 'tampered'")]
    [InlineData("DELETE FROM audit_event")]
    [InlineData("TRUNCATE audit_event")]
    public async Task AuditEvent_OwnerWritesOverAnExistingRow_IsRefusedByTrigger(string statement)
    {
        // Arrange
        await PostgresSchema.ApplyAsync(DataSource, Token);
        await InsertOneEventAsync();

        // Act
        var refusal = await Record.ExceptionAsync(() => ExecuteAsync(statement));

        // Assert
        Assert.Contains("append-only", Assert.IsType<PostgresException>(refusal).MessageText, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task AuditEvent_SuppliedWritePosition_IsRefused()
    {
        // Arrange — the database allocates write_position, so pin that it refuses a supplied one.
        await PostgresSchema.ApplyAsync(DataSource, Token);

        // Act
        var refusal = await Record.ExceptionAsync(() => ExecuteAsync(
            """
            INSERT INTO audit_event (write_position, call_id, event_id, sequence, kind, occurred_at)
            VALUES (1, 'C1', gen_random_uuid(), 1, 'call.started', now())
            """));

        // Assert
        Assert.Equal("428C9", Assert.IsType<PostgresException>(refusal).SqlState);
    }

    [PostgresFact]
    public async Task AuditEvent_SecondRowOnTheSameCallAndSequence_IsRefused()
    {
        // Arrange — raw SQL stands in for the store here, so the table itself is what catches two
        // rows landing on the same call and sequence.
        await PostgresSchema.ApplyAsync(DataSource, Token);
        await InsertOneEventAsync();

        // Act
        var refusal = await Record.ExceptionAsync(() => ExecuteAsync(
            """
            INSERT INTO audit_event (call_id, event_id, sequence, kind, occurred_at)
            VALUES ('C1', gen_random_uuid(), 1, 'call.ended', now())
            """));

        // Assert
        Assert.Equal("23505", Assert.IsType<PostgresException>(refusal).SqlState);
    }

    [PostgresFact]
    public async Task AuditEvent_SessionReplicationRoleIsReplica_BypassesEveryTrigger()
    {
        // Arrange — session_replication_role bypasses all three triggers in one statement with no
        // DDL. Nothing detects that edit; see the fourth design amendment in docs/BUILD.md.
        await PostgresSchema.ApplyAsync(DataSource, Token);
        await ExecuteAsync(
            """
            INSERT INTO audit_event (call_id, event_id, sequence, kind, occurred_at)
            VALUES ('C1', gen_random_uuid(), 0, 'call.started', now())
            """);

        // Act
        await ExecuteAsync(
            """
            SET session_replication_role = replica;
            UPDATE audit_event SET kind = 'call.ended' WHERE sequence = 0;
            SET session_replication_role = origin;
            """);

        // Assert
        Assert.Equal("call.ended", await ScalarAsync<string>("SELECT kind FROM audit_event WHERE sequence = 0"));
    }

    [PostgresFact]
    public async Task CallMessage_SecondMessageOnTheSameOrdinal_IsRefused()
    {
        // Arrange
        await PostgresSchema.ApplyAsync(DataSource, Token);
        await ExecuteAsync("INSERT INTO call (call_id) VALUES ('C1')");
        await ExecuteAsync(
            "INSERT INTO call_message (call_id, ordinal, turn_index, role, content) VALUES ('C1', 0, 0, 'user', '{}')");

        // Act
        var refusal = await Record.ExceptionAsync(() => ExecuteAsync(
            "INSERT INTO call_message (call_id, ordinal, turn_index, role, content) VALUES ('C1', 0, 0, 'assistant', '{}')"));

        // Assert
        Assert.Equal("23505", Assert.IsType<PostgresException>(refusal).SqlState);
    }

    [PostgresFact]
    public async Task Versions_AreOrderedSoTheParentTableIsCreatedFirst()
        => Assert.Equal(
            ["001_writer_role", "002_call", "003_call_message",
             "004_audit_event", "005_audit_event_append_only"],
            PostgresSchema.Versions);

    [PostgresFact]
    public async Task CallMessage_WithNoCallRow_IsRefused()
    {
        // Arrange
        await PostgresSchema.ApplyAsync(DataSource, Token);
        await ExecuteAsync("INSERT INTO call (call_id) VALUES ('present')");

        // Act
        var refusal = await Record.ExceptionAsync(() => ExecuteAsync(
            "INSERT INTO call_message (call_id, ordinal, turn_index, role, content) VALUES ('absent', 0, 0, 'user', '{}')"));

        // Assert
        Assert.Equal("23503", Assert.IsType<PostgresException>(refusal).SqlState);
    }

    /// <summary>
    /// The cascade is the whole reason one store holds both. It reaches call_message and
    /// call_principal, and it deliberately does not reach audit_event: 005 refuses every DELETE
    /// there, and the trail outlives the conversation on purpose.
    /// </summary>
    [PostgresFact]
    public async Task DeletingACall_TakesItsMessagesAndPrincipals_AndLeavesItsAuditEvents()
    {
        // Arrange
        await PostgresSchema.ApplyAsync(DataSource, Token);
        await ExecuteAsync("INSERT INTO call (call_id) VALUES ('C1')");
        await ExecuteAsync(
            "INSERT INTO call_message (call_id, ordinal, turn_index, role, content) VALUES ('C1', 0, 0, 'user', '{}')");
        await ExecuteAsync(
            "INSERT INTO call_principal (call_id, principal_key, role) VALUES ('C1', 'p1', 'owner')");
        await InsertOneEventAsync();

        // Act
        await ExecuteAsync("DELETE FROM call WHERE call_id = 'C1'");

        // Assert
        Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM call_message"));
        Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM call_principal"));
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM audit_event"));
    }

    private Task InsertOneEventAsync() => ExecuteAsync(
        """
        INSERT INTO audit_event (call_id, event_id, sequence, kind, occurred_at)
        VALUES ('C1', gen_random_uuid(), 1, 'call.started', now())
        """);

    /// <summary>Opens a pool that logs in as an ordinary member of <c>agentcore_writer</c>.</summary>
    /// <remarks>
    /// The login role is per-test because roles are cluster-wide. It holds nothing of its own: every
    /// right it has arrives through the membership.
    /// </remarks>
    private async Task<NpgsqlDataSource> OpenAsWriterAsync()
    {
        var login = "agentcore_member_" + Guid.NewGuid().ToString("n");

        await ExecuteAsync($"CREATE ROLE \"{login}\" LOGIN PASSWORD 'member' NOSUPERUSER NOCREATEDB NOCREATEROLE");
        await ExecuteAsync($"GRANT agentcore_writer TO \"{login}\"");

        return NpgsqlDataSource.Create(
            new NpgsqlConnectionStringBuilder(Database.ConnectionString)
            {
                Username = login,
                Password = "member",
            });
    }

}
