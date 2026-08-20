using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Database.Postgres;
using Npgsql;

namespace AgentCore.Infrastructure.Transcript.Postgres;

/// <summary>
/// The <c>postgres</c> transcript vendor behind <see cref="ITranscriptStore"/>.
/// </summary>
/// <remarks>
/// It reads the same <c>postgres-connection-string</c> secret the audit vendor reads, and a document
/// that names both opens a pool for each. Two pools against one database is what keeps the two stores
/// independent: store 1 holds words a caller may have erased, and store 3 holds a chain that may
/// never lose a row.
/// </remarks>
public sealed class PostgresTranscriptStoreAdapter : ITranscriptStoreAdapter
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
    public async ValueTask<ITranscriptStore> OpenAsync(
        VendorProviderConfiguration entry,
        ISecretResolverPort? secrets,
        CancellationToken cancellationToken = default)
    {
        NpgsqlDataSource dataSource = await PostgresDataSourceFactory
            .OpenMigratedAsync(
                secrets,
                "The words of every call in providers.transcript are written to PostgreSQL.",
                cancellationToken)
            .ConfigureAwait(false);

        return new PostgresTranscriptStore(dataSource);
    }
}
