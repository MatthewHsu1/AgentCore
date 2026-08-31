using AgentCore.Infrastructure.Tests.Database.Postgres;
using Npgsql;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Calls.Postgres;

/// <summary>Store 0's shape, and what the running role may do to it.</summary>
/// <remarks>
/// <c>call</c> and <c>role</c> are both non-reserved keywords in PostgreSQL and need no quoting. That
/// is what the first test proves: the migration would fail to apply at all if it were untrue.
/// <para>
/// The primary key's column order is read from the catalogue rather than inferred from behaviour. A
/// listing filters by principal, so principal has to lead, and either order satisfies uniqueness.
/// </para>
/// </remarks>
public sealed class CallThreadSchemaTests : PostgresDatabaseTest
{
    /// <inheritdoc />
    protected override bool Migrated => true;

    [PostgresFact]
    public async Task Migration_Applied_MakesBothTables()
    {
        // Act
        var tables = await ScalarAsync<long>(
            """
            SELECT count(*) FROM information_schema.tables
             WHERE table_schema = current_schema() AND table_name IN ('call', 'call_principal')
            """);

        // Assert
        Assert.Equal(2, tables);
    }

    [PostgresFact]
    public async Task Call_AStatusOutsideTheTwo_IsRefused()
    {
        // Act
        var refusal = await Record.ExceptionAsync(
            () => ExecuteAsync("INSERT INTO call (call_id, status) VALUES ('c1', 'nonsense')"));

        // Assert
        Assert.Equal("23514", Assert.IsType<PostgresException>(refusal).SqlState);
    }

    [PostgresFact]
    public async Task CallPrincipal_ItsCallDeleted_GoesWithIt()
    {
        // Arrange
        await ExecuteAsync("INSERT INTO call (call_id) VALUES ('c1')");
        await ExecuteAsync("INSERT INTO call_principal (call_id, principal_key, role) VALUES ('c1', 'p1', 'caller')");

        // Act
        await ExecuteAsync("DELETE FROM call WHERE call_id = 'c1'");

        // Assert
        Assert.Equal(0, await ScalarAsync<long>("SELECT count(*) FROM call_principal"));
    }

    [PostgresFact]
    public async Task CallPrincipal_TheSamePairTwice_IsRefusedByThePrimaryKey()
    {
        // Arrange
        await ExecuteAsync("INSERT INTO call (call_id) VALUES ('c1')");
        await ExecuteAsync("INSERT INTO call_principal (call_id, principal_key, role) VALUES ('c1', 'p1', 'caller')");

        // Act
        var refusal = await Record.ExceptionAsync(
            () => ExecuteAsync("INSERT INTO call_principal (call_id, principal_key, role) VALUES ('c1', 'p1', 'agent')"));

        // Assert
        Assert.Equal("23505", Assert.IsType<PostgresException>(refusal).SqlState);
    }

    [PostgresFact]
    public async Task CallPrincipal_ItsPrimaryKey_LeadsWithThePrincipal()
    {
        // Act
        var columns = await ScalarAsync<string>(
            """
            SELECT string_agg(a.attname, ',' ORDER BY k.ord)
              FROM pg_index i
              JOIN LATERAL unnest(i.indkey) WITH ORDINALITY AS k(attnum, ord) ON true
              JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum
             WHERE i.indrelid = 'call_principal'::regclass AND i.indisprimary
            """);

        // Assert
        Assert.Equal("principal_key,call_id", columns);
    }

    [PostgresFact]
    public async Task CallPrincipal_TheOtherDirection_HasItsOwnIndex()
    {
        // Act
        var indexes = await ScalarAsync<long>(
            """
            SELECT count(*) FROM pg_indexes
             WHERE schemaname = current_schema()
               AND tablename = 'call_principal'
               AND indexname = 'call_principal_call_idx'
            """);

        // Assert
        Assert.Equal(1, indexes);
    }

    [PostgresTheory]
    [InlineData("call", "SELECT")]
    [InlineData("call", "INSERT")]
    [InlineData("call", "UPDATE")]
    [InlineData("call", "DELETE")]
    [InlineData("call_principal", "SELECT")]
    [InlineData("call_principal", "INSERT")]
    [InlineData("call_principal", "UPDATE")]
    [InlineData("call_principal", "DELETE")]
    public async Task Writer_EachStoreZeroPrivilege_IsGranted(string table, string privilege)
    {
        // Act
        var granted = await ScalarAsync<bool>(
            $"SELECT has_table_privilege('agentcore_writer', '{table}', '{privilege}')");

        // Assert
        Assert.True(granted);
    }
}
