using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Database.Postgres;
using Npgsql;

namespace AgentCore.Infrastructure.Conversation.Postgres
{
    /// <summary>
    /// The <c>postgres</c> conversation vendor behind <see cref="IConversationStore"/>.
    /// </summary>
    public sealed class PostgresConversationStoreAdapter : IConversationStoreAdapter
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
        public async ValueTask<IConversationStore> OpenAsync(
            VendorProviderConfiguration entry,
            ISecretResolverPort? secrets,
            CancellationToken cancellationToken = default)
        {
            NpgsqlDataSource dataSource = await PostgresDataSourceFactory
                .OpenMigratedAsync(
                    secrets,
                    "A conversation's row in providers.conversations is written to PostgreSQL.",
                    cancellationToken)
                .ConfigureAwait(false);

            return new PostgresConversationStore(dataSource);
        }
    }
}
