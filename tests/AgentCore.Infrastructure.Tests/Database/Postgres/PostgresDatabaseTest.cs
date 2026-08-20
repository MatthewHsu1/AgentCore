using Npgsql;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Database.Postgres;

/// <summary>
/// A test that runs against a PostgreSQL database of its own.
/// </summary>
/// <remarks>
/// Every test here needs the same four things — a fresh database, a token, a way to run a statement,
/// and a way to read one value — so they live once. Mark the tests themselves with
/// <see cref="PostgresFactAttribute"/> or <see cref="PostgresTheoryAttribute"/>, which is what makes
/// them skip when the environment names no cluster.
/// </remarks>
public abstract class PostgresDatabaseTest : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    /// <summary>Whether the schema is applied before the test body runs.</summary>
    protected abstract bool Migrated { get; }

    /// <summary>The token every call in a test body carries.</summary>
    protected static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The database this test owns.</summary>
    protected PostgresTestDatabase Database =>
        _database ?? throw new InvalidOperationException("No database was created.");

    /// <summary>The pool that reaches it.</summary>
    protected NpgsqlDataSource DataSource => Database.DataSource;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        if (PostgresCluster.IsConfigured)
        {
            _database = Migrated
                ? await PostgresTestDatabase.CreateMigratedAsync(Token)
                : await PostgresTestDatabase.CreateAsync(Token);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Runs a statement and keeps no result.</summary>
    protected async Task ExecuteAsync(string sql)
    {
        await using var command = DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(Token);
    }

    /// <summary>Runs a statement and reads its first value.</summary>
    protected async Task<T> ScalarAsync<T>(string sql)
    {
        await using var command = DataSource.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync(Token))!;
    }
}
