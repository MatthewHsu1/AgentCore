using Npgsql;

namespace AgentCore.Infrastructure.Database.Postgres;

/// <summary>
/// The PostgreSQL schema store, and the applier that puts it in place.
/// </summary>
public static class PostgresSchema
{
    private const string ResourcePrefix = "AgentCore.Infrastructure.Database.Postgres.Migrations.";

    /// <summary>Guards two processes migrating one database at the same moment.</summary>
    /// <remarks>
    /// The value is arbitrary. It only has to be the same in every process that migrates, so it is a
    /// constant here rather than anything derived.
    /// </remarks>
    private const long ApplyLockKey = 0x41C05CE700000001;

    private const string LedgerName = "agentcore_schema_migration";

    private const string LedgerDdl = """
        CREATE TABLE agentcore_schema_migration (
            version    text        NOT NULL PRIMARY KEY,
            applied_at timestamptz NOT NULL DEFAULT now()
        )
        """;

    /// <summary>Every migration this assembly carries, in the order it is applied.</summary>
    internal static IReadOnlyList<string> Versions { get; } = ReadVersions().AsReadOnly();

    /// <summary>Reads one migration's SQL out of the assembly.</summary>
    /// <param name="version">A value from <see cref="Versions"/>.</param>
    /// <returns>The script text.</returns>
    /// <exception cref="ArgumentException">No migration carries that version.</exception>
    private static string Read(string version)
    {
        ArgumentNullException.ThrowIfNull(version);

        var assembly = typeof(PostgresSchema).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourcePrefix + version + ".sql")
            ?? throw new ArgumentException($"No migration is named '{version}'.", nameof(version));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Applies every migration the database has not seen yet.</summary>
    /// <param name="dataSource">A data source whose role may create tables in its search path.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The versions this call applied, oldest first. Empty when the schema was current.</returns>
    /// <remarks>
    /// The connecting role also needs <c>CREATEROLE</c> the first time, because migration 001 creates
    /// <c>agentcore_writer</c>. The running system connects as a member of that role, not as this one.
    /// </remarks>
    public static async Task<IReadOnlyList<string>> ApplyAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // The lock comes first. Two sessions creating the ledger at once race in the catalogue, and
        // the loser reports a duplicate key on pg_type rather than a clean no-op.
        await using (NpgsqlCommand serialise = new("SELECT pg_advisory_xact_lock($1)", connection, transaction))
        {
            serialise.Parameters.Add(new NpgsqlParameter { Value = ApplyLockKey });
            await serialise.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Ask whether the ledger is there rather than issuing CREATE TABLE IF NOT EXISTS. The running
        // system starts as agentcore_writer, which may create nothing; on an already-migrated database
        // this path then reads twice and writes no DDL, and only the first, privileged run creates.
        // to_regclass answers for a role that holds no right on the table at all.
        var ledgerExists = await ScalarAsync<bool>(
            connection, transaction, $"SELECT to_regclass('{LedgerName}') IS NOT NULL", cancellationToken).ConfigureAwait(false);

        if (!ledgerExists)
        {
            await ExecuteAsync(connection, transaction, LedgerDdl, cancellationToken).ConfigureAwait(false);
        }

        var applied = await ReadAppliedAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        List<string> ran = [];

        foreach (var version in Versions)
        {
            if (applied.Contains(version))
            {
                continue;
            }

            await ExecuteAsync(connection, transaction, Read(version), cancellationToken).ConfigureAwait(false);

            await using var record = new NpgsqlCommand(
                "INSERT INTO agentcore_schema_migration (version) VALUES ($1)", connection, transaction);
            record.Parameters.AddWithValue(version);
            await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            ran.Add(version);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ran;
    }

    private static async Task<HashSet<string>> ReadAppliedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        HashSet<string> applied = new(StringComparer.Ordinal);

        await using var command = new NpgsqlCommand("SELECT version FROM agentcore_schema_migration", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            applied.Add(reader.GetString(0));
        }

        return applied;
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return (T)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static List<string> ReadVersions()
    {
        var names = typeof(PostgresSchema).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                        && name.EndsWith(".sql", StringComparison.Ordinal))
            .Select(name => name[ResourcePrefix.Length..^".sql".Length])
            .ToList();

        names.Sort(StringComparer.Ordinal);
        return names;
    }
}
