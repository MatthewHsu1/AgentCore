using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Database.Postgres;
using Npgsql;

namespace AgentCore.Infrastructure.Calls.Postgres;

/// <summary>
/// The <c>postgres</c> call vendor behind <see cref="ICallStore"/>.
/// </summary>
public sealed class PostgresCallStoreAdapter : ICallStoreAdapter
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
    public async ValueTask<ICallStore> OpenAsync(
        VendorProviderConfiguration entry,
        ISecretResolverPort? secrets,
        CancellationToken cancellationToken = default)
    {
        NpgsqlDataSource dataSource = await PostgresDataSourceFactory
            .OpenMigratedAsync(
                secrets,
                "A call's row in providers.calls is written to PostgreSQL.",
                cancellationToken)
            .ConfigureAwait(false);

        return new PostgresCallStore(dataSource);
    }
}
