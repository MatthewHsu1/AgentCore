using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Secrets;
using AgentCore.Application.Transcript;
using AgentCore.Infrastructure.Tests.Database.Postgres;
using AgentCore.Infrastructure.Tests.Fakes;
using AgentCore.Infrastructure.Transcript.Postgres;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Transcript.Postgres;

/// <summary>
/// The <c>postgres</c> transcript vendor, which is also where the schema is applied.
/// </summary>
public sealed class PostgresTranscriptStoreAdapterTests : PostgresDatabaseTest
{
    private static readonly VendorProviderConfiguration Entry =
        new() { Kind = PostgresTranscriptStoreAdapter.ProviderKind };

    /// <inheritdoc />
    protected override bool Migrated => false;

    [Fact]
    public void Kind_Always_IsPostgres()
    {
        // Arrange
        PostgresTranscriptStoreAdapter adapter = new();

        // Act
        var kind = adapter.Kind;

        // Assert
        Assert.Equal("postgres", kind);
    }

    [Fact]
    public async Task OpenAsync_NoConnectionStringAnywhere_FailsTheStart()
    {
        // Arrange — a missing credential stops the host, and never a call.
        PostgresTranscriptStoreAdapter adapter = new();
        ISecretResolverPort empty = new MapSecretResolver();

        // Act
        var failure = await Record.ExceptionAsync(() => adapter.OpenAsync(Entry, empty, Token).AsTask());

        // Assert
        Assert.IsType<SecretResolutionException>(failure);
    }

    [PostgresFact]
    public async Task OpenAsync_UnmigratedDatabase_AppliesTheSchema()
    {
        // Arrange
        PostgresTranscriptStoreAdapter adapter = new();

        // Act
        await using var store = (PostgresTranscriptStore)await adapter.OpenAsync(Entry, Resolver(), Token);

        // Assert
        Assert.True(await ScalarAsync<bool>("SELECT to_regclass('call_message') IS NOT NULL"));
    }

    [PostgresFact]
    public async Task OpenAsync_UnmigratedDatabase_ReturnsAStoreThatAppends()
    {
        // Arrange
        PostgresTranscriptStoreAdapter adapter = new();
        await using var store = (PostgresTranscriptStore)await adapter.OpenAsync(Entry, Resolver(), Token);

        // Act
        await store.AppendAsync(
            [new CallMessage("C1", 0, 0, new ChatMessage(ChatRole.User, "what about order 41"))],
            Token);

        // Assert
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM call_message"));
    }

    private MapSecretResolver Resolver() =>
        new MapSecretResolver().With(KnownSecrets.PostgresConnectionStringName, Database.ConnectionString);
}
