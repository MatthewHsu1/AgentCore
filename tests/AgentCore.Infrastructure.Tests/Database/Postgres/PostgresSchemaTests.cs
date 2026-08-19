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
        await InsertGenesisAsync();

        // Act
        var refusal = await Record.ExceptionAsync(() => ExecuteAsync(statement));

        // Assert
        Assert.Contains("append-only", Assert.IsType<PostgresException>(refusal).MessageText, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task AuditEvent_SuppliedChainPosition_IsRefused()
    {
        // Arrange — step 7 rests on the database allocating chain_position, so pin that it refuses one.
        await PostgresSchema.ApplyAsync(DataSource, Token);

        // Act
        var refusal = await Record.ExceptionAsync(() => ExecuteAsync(
            """
            INSERT INTO audit_event (chain_position, call_id, sequence, kind, occurred_at, previous_hash, hash)
            VALUES (1, 'C1', 1, 'call.started', now(), repeat('0', 64), repeat('a', 64))
            """));

        // Assert
        Assert.Equal("428C9", Assert.IsType<PostgresException>(refusal).SqlState);
    }

    [PostgresFact]
    public async Task AuditEvent_SecondRowOnTheSamePreviousHash_IsRefused()
    {
        // Arrange — one link may have one successor, or the chain forks.
        await PostgresSchema.ApplyAsync(DataSource, Token);
        await InsertGenesisAsync();

        // Act
        var refusal = await Record.ExceptionAsync(() => ExecuteAsync(
            """
            INSERT INTO audit_event (call_id, sequence, kind, occurred_at, previous_hash, hash)
            VALUES ('C1', 2, 'call.ended', now(), repeat('0', 64), repeat('b', 64))
            """));

        // Assert
        Assert.Equal("23505", Assert.IsType<PostgresException>(refusal).SqlState);
    }

    [PostgresFact]
    public async Task AuditEvent_SessionReplicationRoleIsReplica_BypassesEveryTrigger()
    {
        // Arrange — session_replication_role bypasses all three triggers in one statement with no
        // DDL, which is why the triggers are a guard rail and AuditChain.Verify is a defence.
        await PostgresSchema.ApplyAsync(DataSource, Token);
        await ExecuteAsync(
            """
            INSERT INTO audit_event (call_id, sequence, kind, occurred_at, previous_hash, hash)
            VALUES ('C1', 0, 'call.started', now(), repeat('0', 64), repeat('a', 64))
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
        await ExecuteAsync(
            "INSERT INTO call_message (call_id, ordinal, turn_index, role, content) VALUES ('C1', 0, 0, 'user', '{}')");

        // Act
        var refusal = await Record.ExceptionAsync(() => ExecuteAsync(
            "INSERT INTO call_message (call_id, ordinal, turn_index, role, content) VALUES ('C1', 0, 0, 'assistant', '{}')"));

        // Assert
        Assert.Equal("23505", Assert.IsType<PostgresException>(refusal).SqlState);
    }

    private Task InsertGenesisAsync() => ExecuteAsync(
        """
        INSERT INTO audit_event (call_id, sequence, kind, occurred_at, previous_hash, hash)
        VALUES ('C1', 1, 'call.started', now(), repeat('0', 64), repeat('a', 64))
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
