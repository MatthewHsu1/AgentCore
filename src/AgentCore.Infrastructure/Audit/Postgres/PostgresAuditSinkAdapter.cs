using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Secrets;
using AgentCore.Infrastructure.Database.Postgres;
using Npgsql;

namespace AgentCore.Infrastructure.Audit.Postgres;

/// <summary>
/// The <c>postgres</c> audit vendor behind <see cref="IAuditSinkPort"/>.
/// </summary>
public sealed class PostgresAuditSinkAdapter : IAuditSinkAdapter
{
    /// <summary>
    /// The one <c>kind</c> value this adapter serves.
    /// </summary>
    public const string ProviderKind = "postgres";

    /// <summary>
    /// Gets the one <c>kind</c> value this adapter serves.
    /// </summary>
    public string Kind => ProviderKind;

    /// <inheritdoc />
    public async ValueTask<IAuditSinkPort> OpenAsync(
        VendorProviderConfiguration entry,
        ISecretResolverPort? secrets,
        CancellationToken cancellationToken = default)
    {
        var connectionString = await secrets
            .RequireAsync(
                KnownSecrets.PostgresConnectionString,
                "The audit chain of providers.audit is written to PostgreSQL.",
                cancellationToken)
            .ConfigureAwait(false);

        NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);

        try
        {
            await PostgresSchema.ApplyAsync(dataSource, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The sink is what owns the pool, and it was never built.
            await dataSource.DisposeAsync().ConfigureAwait(false);

            throw;
        }

        return new PostgresAuditSink(dataSource);
    }
}
