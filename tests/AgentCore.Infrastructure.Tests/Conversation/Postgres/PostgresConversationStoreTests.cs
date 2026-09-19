using System.Text.Json;
using AgentCore.Application.Conversation;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Conversation.Postgres;
using AgentCore.Infrastructure.Tests.Database.Postgres;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Conversation.Postgres;

/// <summary>Store 0, in PostgreSQL.</summary>
public sealed class PostgresConversationStoreTests : PostgresDatabaseTest
{
    /// <inheritdoc />
    protected override bool Migrated => true;

    [PostgresFact]
    public async Task CreateAsync_ANewConversation_WritesOneRow()
    {
        // Arrange
        PostgresConversationStore store = new(DataSource);

        // Act
        var conversation = await store.CreateAsync("c1", Token);

        // Assert
        Assert.Equal("c1", conversation.ConversationId);
        Assert.Null(conversation.Title);
        Assert.Equal(ConversationStatus.Regular, conversation.Status);
        Assert.Equal(1, await ScalarAsync<long>("SELECT count(*) FROM agentcore.conversation"));
    }

    [PostgresFact]
    public async Task CreateAsync_TheSameIdTwice_StaysOneRowAndKeepsTheFirst()
    {
        // Arrange
        PostgresConversationStore store = new(DataSource);

        // Act
        var first = await store.CreateAsync("c1", Token);
        await store.RenameAsync("c1", "kept", Token);
        var second = await store.CreateAsync("c1", Token);

        // Assert
        Assert.Equal(first.CreatedAt, second.CreatedAt);
        Assert.Equal("kept", second.Title);
        Assert.Equal(1, await ScalarAsync<long>("SELECT count(*) FROM agentcore.conversation"));
    }

    [PostgresFact]
    public async Task GetAsync_AConversationThatWasNeverMade_IsNull()
    {
        // Arrange
        PostgresConversationStore store = new(DataSource);

        // Act
        var found = await store.GetAsync("missing", Token);

        // Assert
        Assert.Null(found);
    }

    [PostgresFact]
    public async Task GetAsync_AConversationWithNoMessages_ReportsNoLastActivity()
    {
        // Arrange
        PostgresConversationStore store = new(DataSource);
        await store.CreateAsync("c1", Token);

        // Act
        var found = await store.GetAsync("c1", Token);

        // Assert
        Assert.Null(found!.LastMessageAt);
    }

    [PostgresFact]
    public async Task GetAsync_AConversationWithMessages_ReportsTheNewestMessageTime()
    {
        // Arrange
        PostgresConversationStore store = new(DataSource);
        await store.CreateAsync("c1", Token);
        await ExecuteAsync(
            """
            INSERT INTO agentcore.conversation_message (conversation_id, ordinal, turn_index, role, content, message_id, created_at, updated_at)
            VALUES ('c1', 1, 0, 'user', '{}'::jsonb, 'm1', now(), now() + interval '1 hour')
            """);

        // Act
        var found = await store.GetAsync("c1", Token);

        // Assert
        Assert.True(found!.LastMessageAt > found.CreatedAt);
    }

    [PostgresFact]
    public async Task RenameAsync_AConversation_ChangesOnlyItsTitle()
    {
        // Arrange
        PostgresConversationStore store = new(DataSource);
        await store.CreateAsync("c1", Token);

        // Act
        await store.RenameAsync("c1", "A squeaky belt", Token);

        // Assert
        var found = await store.GetAsync("c1", Token);
        Assert.Equal("A squeaky belt", found!.Title);
        Assert.Equal(ConversationStatus.Regular, found.Status);
    }

    [PostgresFact]
    public async Task SetStatusAsync_Archived_IsReadBack()
    {
        // Arrange
        PostgresConversationStore store = new(DataSource);
        await store.CreateAsync("c1", Token);

        // Act
        await store.SetStatusAsync("c1", ConversationStatus.Archived, Token);

        // Assert
        Assert.Equal(ConversationStatus.Archived, (await store.GetAsync("c1", Token))!.Status);
    }

    [PostgresFact]
    public async Task SetCustomAsync_SomeFields_AreReadBackWhole()
    {
        // Arrange
        PostgresConversationStore store = new(DataSource);
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
        PostgresConversationStore store = new(DataSource);
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
        PostgresConversationStore store = new(DataSource);
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
        PostgresConversationStore store = new(DataSource);
        await store.CreateAsync("c1", Token);
        await store.SetExternalIdAsync("c1", "crm-77", Token);

        // Act
        await store.SetExternalIdAsync("c1", null, Token);

        // Assert
        Assert.Null((await store.GetAsync("c1", Token))!.ExternalId);
    }

    [PostgresFact]
    public async Task DeleteAsync_AConversation_LeavesNoRowAndNoClaim()
    {
        // Arrange
        PostgresConversationStore store = new(DataSource);
        await store.CreateAsync("c1", Token);
        await store.AttachPrincipalAsync("c1", "person-a", "caller", Token);

        // Act
        await store.DeleteAsync("c1", Token);

        // Assert
        Assert.Equal(0, await ScalarAsync<long>("SELECT count(*) FROM agentcore.conversation"));
        Assert.Equal(0, await ScalarAsync<long>("SELECT count(*) FROM agentcore.conversation_principal"));
    }

    [PostgresFact]
    public async Task DeleteAsync_AConversation_TakesItsSameIdContinuationWithIt()
    {
        // Arrange — one id plays three roles now, so the row delete takes the key with it. Per-turn
        // response ids are gone with their own rows only through DeleteContinuationAsync; nothing
        // here enumerates them.
        PostgresConversationStore store = new(DataSource);
        await store.CreateAsync("c1", Token);
        using var document = System.Text.Json.JsonDocument.Parse("""{ "conversationId": "c1" }""");
        await store.SaveContinuationAsync("c1", document.RootElement, Token);

        // Act
        await store.DeleteAsync("c1", Token);

        // Assert
        Assert.Null(await store.GetContinuationAsync("c1", Token));
    }

    [PostgresFact]
    public async Task DeleteAsync_AConversationThatWasNeverMade_IsNotAThrow()
    {
        // Arrange
        PostgresConversationStore store = new(DataSource);

        // Act
        var thrown = await Record.ExceptionAsync(() => store.DeleteAsync("missing", Token).AsTask());

        // Assert
        Assert.Null(thrown);
    }
}
