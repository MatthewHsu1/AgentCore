using AgentCore.Infrastructure.Database.Postgres;
using Npgsql;

namespace AgentCore.Infrastructure.Tests.Database.Postgres;

/// <summary>
/// A database of its own for one test, dropped when the test ends.
/// </summary>
/// <remarks>
/// A migration is not a thing two tests can share, and neither is a hash chain: the head of one is
/// global to the table. So every test that touches either gets an empty database and takes it away
/// again. Roles are cluster-wide and outlive it, which is what the migration's create-if-absent
/// handler is for.
/// </remarks>
public sealed class PostgresTestDatabase : IAsyncDisposable
{
    private readonly string _name;

    private PostgresTestDatabase(string name, string connectionString, NpgsqlDataSource dataSource)
    {
        _name = name;
        ConnectionString = connectionString;
        DataSource = dataSource;
    }

    /// <summary>The pool that reaches the new database.</summary>
    public NpgsqlDataSource DataSource { get; }

    /// <summary>The connection string, password and all.</summary>
    /// <remarks>
    /// <see cref="NpgsqlDataSource.ConnectionString"/> is not this: Npgsql redacts the password out of
    /// it unless the string asked to persist security info, so a test that hands it to an adapter
    /// hands over one that cannot log in.
    /// </remarks>
    public string ConnectionString { get; }

    /// <summary>Creates an empty database on the cluster the environment names.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The database, which the caller disposes.</returns>
    public static async Task<PostgresTestDatabase> CreateAsync(CancellationToken cancellationToken)
    {
        var cluster = PostgresCluster.ConnectionString
            ?? throw new InvalidOperationException(PostgresCluster.SkipReason);

        var name = "agentcore_test_" + Guid.NewGuid().ToString("n");

        await using (NpgsqlConnection admin = new(cluster))
        {
            await admin.OpenAsync(cancellationToken);
            await using NpgsqlCommand create = new($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        var connectionString = new NpgsqlConnectionStringBuilder(cluster) { Database = name }.ConnectionString;

        return new PostgresTestDatabase(name, connectionString, NpgsqlDataSource.Create(connectionString));
    }

    /// <summary>Creates an empty database and applies the schema to it.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The migrated database, which the caller disposes.</returns>
    public static async Task<PostgresTestDatabase> CreateMigratedAsync(CancellationToken cancellationToken)
    {
        PostgresTestDatabase database = await CreateAsync(cancellationToken);
        await PostgresSchema.ApplyAsync(database.DataSource, cancellationToken);
        return database;
    }

    /// <summary>Closes the pool and drops the database.</summary>
    /// <returns>A task that completes when the database is gone.</returns>
    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync();

        await using NpgsqlConnection admin = new(PostgresCluster.ConnectionString);
        await admin.OpenAsync();
        await using NpgsqlCommand drop = new($"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
    }
}
