using System.Reflection;
using Npgsql;

namespace AgentCore.Infrastructure.Database.Postgres
{
    /// <summary>
    /// The PostgreSQL schema store, and the applier that puts it in place.
    /// </summary>
    public static class PostgresSchema
    {
        private const string ResourcePrefix = "AgentCore.Infrastructure.Database.Postgres.Migrations.";

        /// <summary>The schema every AgentCore table lives in.</summary>
        internal const string SchemaName = "agentcore";

        /// <summary>Guards two processes migrating one database at the same moment.</summary>
        /// <remarks>
        /// The value is arbitrary. It only has to be the same in every process that migrates, so it is a
        /// constant here rather than anything derived.
        /// </remarks>
        private const long ApplyLockKey = 0x41C05CE700000001;

        private const string LedgerName = $"{SchemaName}.schema_migration";

        private const string LedgerDdl = $"""
        CREATE TABLE {LedgerName} (
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

            Assembly assembly = typeof(PostgresSchema).Assembly;
            using Stream stream = assembly.GetManifestResourceStream(ResourcePrefix + version + ".sql")
                ?? throw new ArgumentException($"No migration is named '{version}'.", nameof(version));
            using StreamReader reader = new(stream);
            return reader.ReadToEnd();
        }

        /// <summary>Applies every migration the database has not seen yet.</summary>
        /// <param name="dataSource">A data source whose role may create the <c>agentcore</c> schema and tables in it.</param>
        /// <param name="cancellationToken">Cancels the work.</param>
        /// <returns>The versions this conversation applied, oldest first. Empty when the schema was current.</returns>
        /// <remarks>
        /// The connecting role also needs <c>CREATEROLE</c> the first time, because migration 001 creates
        /// <c>agentcore_writer</c>. The running system connects as a member of that role, not as this one, so a
        /// release that adds a migration is applied by the owning role before the running system starts on it.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// A migration is pending and this connection's role may not apply it, as a member of
        /// <c>agentcore_writer</c> may not. Nothing was applied, and nothing is skipped: the message names the
        /// pending migrations and the role that must apply them. The inner exception is the database's refusal.
        /// </exception>
        public static async Task<IReadOnlyList<string>> ApplyAsync(
            NpgsqlDataSource dataSource,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(dataSource);

            await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            // The lock comes first. Two sessions creating the ledger at once race in the catalogue, and
            // the loser reports a duplicate key on pg_type rather than a clean no-op.
            await using (NpgsqlCommand serialise = new("SELECT pg_advisory_xact_lock($1)", connection, transaction))
            {
                _ = serialise.Parameters.Add(new NpgsqlParameter { Value = ApplyLockKey });
                _ = await serialise.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // Until the ledger is read, every migration is pending as far as this connection knows.
            IReadOnlyList<string> pending = Versions;
            try
            {
                pending = await PendingAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

                foreach (string version in pending)
                {
                    await ExecuteAsync(connection, transaction, Read(version), cancellationToken).ConfigureAwait(false);

                    await using NpgsqlCommand record = new(
                        $"INSERT INTO {LedgerName} (version) VALUES ($1)", connection, transaction);
                    _ = record.Parameters.AddWithValue(version);
                    _ = await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (PostgresException refusal) when (refusal.SqlState == PostgresErrorCodes.InsufficientPrivilege)
            {
                string applier = pending.Contains(Versions[0])
                    ? $"a role that may create the '{SchemaName}' schema in this database, and may create roles if "
                        + "agentcore_writer does not exist yet"
                    : $"the role that applied {Versions[0]}, the owner of the '{SchemaName}' schema";

                throw new InvalidOperationException(
                    $"The database is missing the AgentCore schema migration(s) {string.Join(", ", pending)}, and the role "
                    + $"'{connection.UserName}' may not apply them, so this host will not start on it. Apply them as "
                    + $"{applier}: call PostgresSchema.ApplyAsync with a data source that connects as that role, or start "
                    + "one host with that role's connection string. Then start this host again.",
                    refusal);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return pending;
        }

        /// <summary>Creates the schema and the ledger when they are missing, and reads which migrations are not applied yet.</summary>
        private static async Task<List<string>> PendingAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
        {
            bool schemaExists = await ScalarAsync<bool>(
                connection, transaction, $"SELECT to_regnamespace('{SchemaName}') IS NOT NULL", cancellationToken).ConfigureAwait(false);

            if (!schemaExists)
            {
                await ExecuteAsync(connection, transaction, $"CREATE SCHEMA {SchemaName}", cancellationToken).ConfigureAwait(false);
            }

            // Ask whether the ledger is there rather than issuing CREATE TABLE IF NOT EXISTS. The running
            // system starts as agentcore_writer, which may create nothing; on an already-migrated database
            // this path then reads twice and writes no DDL, and only the first, privileged run creates.
            // to_regclass answers for a role that holds no right on the table at all.
            bool ledgerExists = await ScalarAsync<bool>(
                connection, transaction, $"SELECT to_regclass('{LedgerName}') IS NOT NULL", cancellationToken).ConfigureAwait(false);

            if (!ledgerExists)
            {
                await ExecuteAsync(connection, transaction, LedgerDdl, cancellationToken).ConfigureAwait(false);
            }

            HashSet<string> applied = await ReadAppliedAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

            return [.. Versions.Where(version => !applied.Contains(version))];
        }

        private static async Task<HashSet<string>> ReadAppliedAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
        {
            HashSet<string> applied = new(StringComparer.Ordinal);

            await using NpgsqlCommand command = new($"SELECT version FROM {LedgerName}", connection, transaction);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                _ = applied.Add(reader.GetString(0));
            }

            return applied;
        }

        private static async Task<T> ScalarAsync<T>(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string sql,
            CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new(sql, connection, transaction);
            return (T)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        private static async Task ExecuteAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string sql,
            CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new(sql, connection, transaction);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private static List<string> ReadVersions()
        {
            List<string> names = [.. typeof(PostgresSchema).Assembly.GetManifestResourceNames()
                .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                            && name.EndsWith(".sql", StringComparison.Ordinal))
                .Select(name => name[ResourcePrefix.Length..^".sql".Length])];

            names.Sort(StringComparer.Ordinal);
            return names;
        }
    }
}
