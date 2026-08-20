using AgentCore.Application.Ports;
using AgentCore.Application.Secrets;
using Npgsql;

namespace AgentCore.Infrastructure.Database.Postgres;

/// <summary>
/// Opens a migrated pool for the vendors that keep something in PostgreSQL.
/// </summary>
/// <remarks>
/// The audit chain and the transcript are two stores and one database, and each adapter opened it the
/// same way: resolve the one connection-string secret, create the pool, apply the schema, and drop the
/// pool if the schema will not apply. That sequence lives here so a third store cannot get it subtly
/// different, and so the failure a deployment sees is one failure and not two.
/// </remarks>
internal static class PostgresDataSourceFactory
{
    /// <summary>Resolves the connection string, opens the pool, and applies the schema to it.</summary>
    /// <param name="secrets">The chain the credential resolves through, or <see langword="null"/>.</param>
    /// <param name="purpose">
    /// What this pool is for, written into the failure a deployment reads when the secret is missing.
    /// </param>
    /// <param name="cancellationToken">Cancels the resolve and the migration.</param>
    /// <returns>The pool, which the caller then owns and disposes.</returns>
    /// <exception cref="SecretResolutionException">The connection string resolves to nothing.</exception>
    /// <remarks>
    /// This runs once, while the host starts, so a missing credential or an unreachable database stops
    /// the host and never a call.
    /// </remarks>
    internal static async ValueTask<NpgsqlDataSource> OpenMigratedAsync(
        ISecretResolverPort? secrets,
        string purpose,
        CancellationToken cancellationToken)
    {
        var connectionString = await secrets
            .RequireAsync(KnownSecrets.PostgresConnectionString, purpose, cancellationToken)
            .ConfigureAwait(false);

        NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);

        try
        {
            await PostgresSchema.ApplyAsync(dataSource, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The store that would have owned this pool was never built.
            await dataSource.DisposeAsync().ConfigureAwait(false);

            throw;
        }

        return dataSource;
    }
}
