using System.Text.Json;
using AgentCore.Application.Calls;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Calls.Postgres;
using AgentCore.Infrastructure.Tests.Database.Postgres;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Calls.Postgres;

/// <summary>Store 0, in PostgreSQL.</summary>
public sealed class PostgresCallStoreTests : PostgresDatabaseTest
{
    /// <inheritdoc />
    protected override bool Migrated => true;

    [PostgresFact]
    public async Task CreateAsync_ANewCall_WritesOneRow()
    {
        // Arrange
        PostgresCallStore store = new(DataSource);

        // Act
        var call = await store.CreateAsync("c1", Token);

        // Assert
        Assert.Equal("c1", call.CallId);
        Assert.Null(call.Title);
        Assert.Equal(CallStatus.Regular, call.Status);
        Assert.Equal(1, await ScalarAsync<long>("SELECT count(*) FROM call"));
    }

    [PostgresFact]
    public async Task CreateAsync_TheSameIdTwice_StaysOneRowAndKeepsTheFirst()
    {
        // Arrange
        PostgresCallStore store = new(DataSource);

        // Act
        var first = await store.CreateAsync("c1", Token);
        await store.RenameAsync("c1", "kept", Token);
        var second = await store.CreateAsync("c1", Token);

        // Assert
        Assert.Equal(first.CreatedAt, second.CreatedAt);
        Assert.Equal("kept", second.Title);
        Assert.Equal(1, await ScalarAsync<long>("SELECT count(*) FROM call"));
    }

    [PostgresFact]
    public async Task GetAsync_ACallThatWasNeverMade_IsNull()
    {
        // Arrange
        PostgresCallStore store = new(DataSource);

        // Act
        var found = await store.GetAsync("missing", Token);

        // Assert
        Assert.Null(found);
    }

    [PostgresFact]
    public async Task GetAsync_ACallWithNoMessages_ReportsNoLastActivity()
    {
        // Arrange
        PostgresCallStore store = new(DataSource);
        await store.CreateAsync("c1", Token);

        // Act
        var found = await store.GetAsync("c1", Token);

        // Assert
        Assert.Null(found!.LastMessageAt);
    }

    [PostgresFact]
    public async Task GetAsync_ACallWithMessages_ReportsTheNewestMessageTime()
    {
        // Arrange
        PostgresCallStore store = new(DataSource);
        await store.CreateAsync("c1", Token);
        await ExecuteAsync(
            """
            INSERT INTO call_message (call_id, ordinal, turn_index, role, content, created_at, updated_at)
            VALUES ('c1', 1, 0, 'user', '{}'::jsonb, now(), now() + interval '1 hour')
            """);

        // Act
        var found = await store.GetAsync("c1", Token);

        // Assert
        Assert.True(found!.LastMessageAt > found.CreatedAt);
    }

    [PostgresFact]
    public async Task RenameAsync_ACall_ChangesOnlyItsTitle()
    {
        // Arrange
        PostgresCallStore store = new(DataSource);
        await store.CreateAsync("c1", Token);

        // Act
        await store.RenameAsync("c1", "A squeaky belt", Token);

        // Assert
        var found = await store.GetAsync("c1", Token);
        Assert.Equal("A squeaky belt", found!.Title);
        Assert.Equal(CallStatus.Regular, found.Status);
    }

    [PostgresFact]
    public async Task SetStatusAsync_Archived_IsReadBack()
    {
        // Arrange
        PostgresCallStore store = new(DataSource);
        await store.CreateAsync("c1", Token);

        // Act
        await store.SetStatusAsync("c1", CallStatus.Archived, Token);

        // Assert
        Assert.Equal(CallStatus.Archived, (await store.GetAsync("c1", Token))!.Status);
    }

    [PostgresFact]
    public async Task SetCustomAsync_SomeFields_AreReadBackWhole()
    {
        // Arrange
        PostgresCallStore store = new(DataSource);
        await store.CreateAsync("c1", Token);
        using var document = JsonDocument.Parse("""{"crmId":"A-1","tags":["belt"]}""");

        // Act
        await store.SetCustomAsync("c1", document.RootElement, Token);

        // Assert
        var found = await store.GetAsync("c1", Token);
        Assert.Equal("A-1", found!.Custom!.Value.GetProperty("crmId").GetString());
    }

    [PostgresFact]
    public async Task SetCustomAsync_Null_ClearsTheFields()
    {
        // Arrange
        PostgresCallStore store = new(DataSource);
        await store.CreateAsync("c1", Token);
        using var document = JsonDocument.Parse("""{"crmId":"A-1"}""");
        await store.SetCustomAsync("c1", document.RootElement, Token);

        // Act
        await store.SetCustomAsync("c1", null, Token);

        // Assert
        Assert.Null((await store.GetAsync("c1", Token))!.Custom);
    }

    [PostgresFact]
    public async Task SetExternalIdAsync_AConsumersOwnId_IsReadBack()
    {
        // Arrange
        PostgresCallStore store = new(DataSource);
        await store.CreateAsync("c1", Token);

        // Act
        await store.SetExternalIdAsync("c1", "crm-77", Token);

        // Assert
        Assert.Equal("crm-77", (await store.GetAsync("c1", Token))!.ExternalId);
    }

    [PostgresFact]
    public async Task SetExternalIdAsync_Null_ClearsTheId()
    {
        // Arrange
        PostgresCallStore store = new(DataSource);
        await store.CreateAsync("c1", Token);
        await store.SetExternalIdAsync("c1", "crm-77", Token);

        // Act
        await store.SetExternalIdAsync("c1", null, Token);

        // Assert
        Assert.Null((await store.GetAsync("c1", Token))!.ExternalId);
    }

    /// <remarks>
    /// The words are not asserted on. <see cref="ICallStore.DeleteAsync"/> erases store 0 alone, and
    /// erasing store 1 first is the caller's ordering.
    /// </remarks>
    [PostgresFact]
    public async Task DeleteAsync_ACall_LeavesNoRowAndNoClaim()
    {
        // Arrange
        PostgresCallStore store = new(DataSource);
        await store.CreateAsync("c1", Token);
        await store.AttachPrincipalAsync("c1", "person-a", "caller", Token);

        // Act
        await store.DeleteAsync("c1", Token);

        // Assert
        Assert.Equal(0, await ScalarAsync<long>("SELECT count(*) FROM call"));
        Assert.Equal(0, await ScalarAsync<long>("SELECT count(*) FROM call_principal"));
    }

    [PostgresFact]
    public async Task DeleteAsync_ACallThatWasNeverMade_IsNotAThrow()
    {
        // Arrange
        PostgresCallStore store = new(DataSource);

        // Act
        var thrown = await Record.ExceptionAsync(() => store.DeleteAsync("missing", Token).AsTask());

        // Assert
        Assert.Null(thrown);
    }
}
