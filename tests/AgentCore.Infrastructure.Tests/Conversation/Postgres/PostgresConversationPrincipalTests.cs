using AgentCore.Infrastructure.Conversation.Postgres;
using AgentCore.Infrastructure.Tests.Database.Postgres;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Conversation.Postgres;

/// <summary>Who may see a conversation.</summary>
public sealed class PostgresConversationPrincipalTests : PostgresDatabaseTest
{
    /// <inheritdoc />
    protected override bool Migrated => true;

    [PostgresFact]
    public async Task AttachPrincipalAsync_AKey_WritesOneRow()
    {
        // Arrange
        PostgresConversationStore store = new(DataSource);
        await store.CreateAsync("c1", Token);

        // Act
        await store.AttachPrincipalAsync("c1", "person-a", "caller", Token);

        // Assert
        Assert.Equal(1, await ScalarAsync<long>("SELECT count(*) FROM agentcore.conversation_principal"));
    }

    [PostgresFact]
    public async Task AttachPrincipalAsync_TheSamePairTwice_IsNotAThrowAndStaysOneRow()
    {
        // Arrange
        PostgresConversationStore store = new(DataSource);
        await store.CreateAsync("c1", Token);
        await store.AttachPrincipalAsync("c1", "person-a", "caller", Token);

        // Act
        var thrown = await Record.ExceptionAsync(
            () => store.AttachPrincipalAsync("c1", "person-a", "agent", Token).AsTask());

        // Assert
        Assert.Null(thrown);
        Assert.Equal(1, await ScalarAsync<long>("SELECT count(*) FROM agentcore.conversation_principal"));
    }

    [PostgresFact]
    public async Task AttachPrincipalAsync_TheSamePairTwice_KeepsTheFirstAttachedAt()
    {
        // Arrange
        PostgresConversationStore store = new(DataSource);
        await store.CreateAsync("c1", Token);
        await store.AttachPrincipalAsync("c1", "person-a", "caller", Token);
        var first = await ScalarAsync<DateTime>("SELECT attached_at FROM agentcore.conversation_principal");

        // Act
        await store.AttachPrincipalAsync("c1", "person-a", "caller", Token);

        // Assert
        Assert.Equal(first, await ScalarAsync<DateTime>("SELECT attached_at FROM agentcore.conversation_principal"));
    }

    [PostgresFact]
    public async Task AttachPrincipalAsync_TwoKeysOnOneConversation_BothStay()
    {
        // Arrange
        PostgresConversationStore store = new(DataSource);
        await store.CreateAsync("c1", Token);

        // Act
        await store.AttachPrincipalAsync("c1", "tenant-a", "tenant", Token);
        await store.AttachPrincipalAsync("c1", "person-a", "caller", Token);

        // Assert
        Assert.Equal(2, await ScalarAsync<long>("SELECT count(*) FROM agentcore.conversation_principal"));
    }

    [PostgresFact]
    public async Task DetachPrincipalAsync_OneOfTwoKeys_LeavesTheOther()
    {
        // Arrange
        PostgresConversationStore store = new(DataSource);
        await store.CreateAsync("c1", Token);
        await store.AttachPrincipalAsync("c1", "tenant-a", "tenant", Token);
        await store.AttachPrincipalAsync("c1", "person-a", "caller", Token);

        // Act
        await store.DetachPrincipalAsync("c1", "person-a", Token);

        // Assert
        Assert.Equal("tenant-a", await ScalarAsync<string>("SELECT principal_key FROM agentcore.conversation_principal"));
    }

    [PostgresFact]
    public async Task DetachPrincipalAsync_AKeyThatWasNeverAttached_IsNotAThrow()
    {
        // Arrange
        PostgresConversationStore store = new(DataSource);
        await store.CreateAsync("c1", Token);

        // Act
        var thrown = await Record.ExceptionAsync(
            () => store.DetachPrincipalAsync("c1", "nobody", Token).AsTask());

        // Assert
        Assert.Null(thrown);
    }
}
