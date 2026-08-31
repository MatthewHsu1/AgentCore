using AgentCore.Infrastructure.Calls.Postgres;
using AgentCore.Infrastructure.Tests.Database.Postgres;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Calls.Postgres;

/// <summary>Who may see a call.</summary>
public sealed class PostgresCallPrincipalTests : PostgresDatabaseTest
{
    /// <inheritdoc />
    protected override bool Migrated => true;

    [PostgresFact]
    public async Task AttachPrincipalAsync_AKey_WritesOneRow()
    {
        // Arrange
        PostgresCallStore store = new(DataSource);
        await store.CreateAsync("c1", Token);

        // Act
        await store.AttachPrincipalAsync("c1", "person-a", "caller", Token);

        // Assert
        Assert.Equal(1, await ScalarAsync<long>("SELECT count(*) FROM call_principal"));
    }

    [PostgresFact]
    public async Task AttachPrincipalAsync_TheSamePairTwice_IsNotAThrowAndStaysOneRow()
    {
        // Arrange
        PostgresCallStore store = new(DataSource);
        await store.CreateAsync("c1", Token);
        await store.AttachPrincipalAsync("c1", "person-a", "caller", Token);

        // Act
        var thrown = await Record.ExceptionAsync(
            () => store.AttachPrincipalAsync("c1", "person-a", "agent", Token).AsTask());

        // Assert
        Assert.Null(thrown);
        Assert.Equal(1, await ScalarAsync<long>("SELECT count(*) FROM call_principal"));
    }

    [PostgresFact]
    public async Task AttachPrincipalAsync_TheSamePairTwice_KeepsTheFirstAttachedAt()
    {
        // Arrange
        PostgresCallStore store = new(DataSource);
        await store.CreateAsync("c1", Token);
        await store.AttachPrincipalAsync("c1", "person-a", "caller", Token);
        var first = await ScalarAsync<DateTime>("SELECT attached_at FROM call_principal");

        // Act
        await store.AttachPrincipalAsync("c1", "person-a", "caller", Token);

        // Assert
        Assert.Equal(first, await ScalarAsync<DateTime>("SELECT attached_at FROM call_principal"));
    }

    [PostgresFact]
    public async Task AttachPrincipalAsync_TwoKeysOnOneCall_BothStay()
    {
        // Arrange
        PostgresCallStore store = new(DataSource);
        await store.CreateAsync("c1", Token);

        // Act
        await store.AttachPrincipalAsync("c1", "tenant-a", "tenant", Token);
        await store.AttachPrincipalAsync("c1", "person-a", "caller", Token);

        // Assert
        Assert.Equal(2, await ScalarAsync<long>("SELECT count(*) FROM call_principal"));
    }

    [PostgresFact]
    public async Task DetachPrincipalAsync_OneOfTwoKeys_LeavesTheOther()
    {
        // Arrange
        PostgresCallStore store = new(DataSource);
        await store.CreateAsync("c1", Token);
        await store.AttachPrincipalAsync("c1", "tenant-a", "tenant", Token);
        await store.AttachPrincipalAsync("c1", "person-a", "caller", Token);

        // Act
        await store.DetachPrincipalAsync("c1", "person-a", Token);

        // Assert
        Assert.Equal("tenant-a", await ScalarAsync<string>("SELECT principal_key FROM call_principal"));
    }

    [PostgresFact]
    public async Task DetachPrincipalAsync_AKeyThatWasNeverAttached_IsNotAThrow()
    {
        // Arrange
        PostgresCallStore store = new(DataSource);
        await store.CreateAsync("c1", Token);

        // Act
        var thrown = await Record.ExceptionAsync(
            () => store.DetachPrincipalAsync("c1", "nobody", Token).AsTask());

        // Assert
        Assert.Null(thrown);
    }
}
