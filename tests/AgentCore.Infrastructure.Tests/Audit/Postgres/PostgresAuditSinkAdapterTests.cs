using AgentCore.TestSupport;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Secrets;
using AgentCore.Infrastructure.Audit.Postgres;
using AgentCore.Infrastructure.Tests.Database.Postgres;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Audit.Postgres
{
    /// <summary>
    /// The <c>postgres</c> audit vendor, which is also where the schema is applied.
    /// </summary>
    public sealed class PostgresAuditSinkAdapterTests : PostgresDatabaseTest
    {
        private static readonly VendorProviderConfiguration Entry =
            new() { Kind = PostgresAuditSinkAdapter.ProviderKind };

        /// <inheritdoc />
        protected override bool Migrated => false;

        [Fact]
        public void Kind_Always_IsPostgres()
        {
            // Arrange
            PostgresAuditSinkAdapter adapter = new();

            // Act
            string kind = adapter.Kind;

            // Assert
            Assert.Equal("postgres", kind);
        }

        [Fact]
        public async Task OpenAsync_NoConnectionStringAnywhere_FailsTheStart()
        {
            // Arrange
            PostgresAuditSinkAdapter adapter = new();
            ISecretResolverPort empty = new MapSecretResolver();

            // Act
            Exception? failure = await Record.ExceptionAsync(() => adapter.OpenAsync(Entry, empty, Token).AsTask());

            // Assert
            _ = Assert.IsType<SecretResolutionException>(failure);
        }

        [PostgresFact]
        public async Task OpenAsync_UnmigratedDatabase_AppliesTheSchema()
        {
            // Arrange
            PostgresAuditSinkAdapter adapter = new();
            ISecretResolverPort secrets = Resolver();

            // Act
            await using PostgresAuditSink sink = (PostgresAuditSink)await adapter.OpenAsync(Entry, secrets, Token);

            // Assert
            Assert.True(await ScalarAsync<bool>("SELECT to_regclass('agentcore.audit_event') IS NOT NULL"));
        }

        [PostgresFact]
        public async Task OpenAsync_UnmigratedDatabase_ReturnsASinkThatAppends()
        {
            // Arrange
            PostgresAuditSinkAdapter adapter = new();
            await using PostgresAuditSink sink = (PostgresAuditSink)await adapter.OpenAsync(Entry, Resolver(), Token);

            // Act
            await sink.AppendAsync(
                new()
                {
                    ConversationId = "C1",
                    EventId = Guid.CreateVersion7(),
                    Kind = Domain.Audit.AuditEventKind.ConversationStarted,
                    OccurredAt = new DateTimeOffset(2026, 8, 19, 9, 0, 0, TimeSpan.Zero),
                },
                Token);

            // Assert
            Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM agentcore.audit_event"));
        }

        private MapSecretResolver Resolver()
        {
            return new MapSecretResolver().With(KnownSecrets.PostgresConnectionStringName, Database.ConnectionString);
        }
    }
}
