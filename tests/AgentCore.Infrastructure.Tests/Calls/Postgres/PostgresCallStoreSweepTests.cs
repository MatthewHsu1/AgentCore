using AgentCore.Application.Transcript;
using AgentCore.Infrastructure.Calls.Postgres;
using AgentCore.Infrastructure.Tests.Database.Postgres;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Calls.Postgres;

/// <summary>
/// Retention, which deletes the call and lets the cascade take the words.
/// </summary>
public sealed class PostgresCallStoreSweepTests : PostgresDatabaseTest
{
    private static readonly TimeSpan Window = TimeSpan.FromDays(30);

    /// <inheritdoc />
    protected override bool Migrated => true;

    private static CallMessage Word(string callId) =>
        new(callId, 0, 0, new ChatMessage(ChatRole.User, "hello"), "m0");

    [PostgresFact]
    public async Task SweepAsync_ACallWhoseLastMessageIsOld_TakesTheCallAndItsWords()
    {
        // Arrange
        PostgresCallStore store = new(DataSource);
        await store.CreateAsync("old", Token);
        await store.AppendAsync([Word("old")], cancellationToken: Token);
        await ExecuteAsync("UPDATE call_message SET updated_at = now() - interval '90 days'");

        // Act
        var swept = await store.SweepAsync(Window, cancellationToken: Token);

        // Assert
        Assert.Equal(1, swept);
        Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM call"));
        Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM call_message"));
    }

    [PostgresFact]
    public async Task SweepAsync_ACallWithNoWordsAtAll_TakesItOnCreatedAt()
    {
        // Arrange
        PostgresCallStore store = new(DataSource);
        await store.CreateAsync("empty", Token);
        await ExecuteAsync("UPDATE call SET created_at = now() - interval '90 days'");

        // Act
        var swept = await store.SweepAsync(Window, cancellationToken: Token);

        // Assert
        Assert.Equal(1, swept);
        Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM call"));
    }

    /// <summary>
    /// created_at is old and the message is fresh, so the message time must win. This is the whole
    /// point of coalescing rather than reading either column alone.
    /// </summary>
    [PostgresFact]
    public async Task SweepAsync_ACallSpokenOnInsideTheWindow_LeavesIt()
    {
        // Arrange
        PostgresCallStore store = new(DataSource);
        await store.CreateAsync("fresh", Token);
        await store.AppendAsync([Word("fresh")], cancellationToken: Token);
        await ExecuteAsync("UPDATE call SET created_at = now() - interval '90 days'");

        // Act
        var swept = await store.SweepAsync(Window, cancellationToken: Token);

        // Assert
        Assert.Equal(0, swept);
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM call"));
    }

    [PostgresFact]
    public async Task SweepAsync_ASweptCall_LeavesItsAuditEvents()
    {
        // Arrange
        PostgresCallStore store = new(DataSource);
        await store.CreateAsync("old", Token);
        await ExecuteAsync("UPDATE call SET created_at = now() - interval '90 days'");
        await ExecuteAsync(
            """
            INSERT INTO audit_event (call_id, event_id, sequence, kind, occurred_at)
            VALUES ('old', gen_random_uuid(), 0, 'call.started', now())
            """);

        // Act
        await store.SweepAsync(Window, cancellationToken: Token);

        // Assert
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM audit_event"));
    }

    [PostgresFact]
    public async Task SweepAsync_MoreExpiredCallsThanOneBatch_LoopsUntilNoneAreLeft()
    {
        // Arrange
        PostgresCallStore store = new(DataSource);
        for (var i = 0; i < 5; i++)
        {
            await store.CreateAsync($"c{i}", Token);
        }

        await ExecuteAsync("UPDATE call SET created_at = now() - interval '90 days'");

        // Act
        var swept = await store.SweepAsync(Window, batchSize: 2, cancellationToken: Token);

        // Assert
        Assert.Equal(5, swept);
        Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM call"));
    }

    [PostgresFact]
    public async Task EraseAsync_ACallsWords_LeavesTheCallItselfListed()
    {
        // Arrange
        PostgresCallStore store = new(DataSource);
        await store.CreateAsync("c1", Token);
        await store.AppendAsync([Word("c1")], cancellationToken: Token);

        // Act
        var erased = await store.EraseAsync("c1", Token);

        // Assert — erase empties a thread that stays; delete takes the thread and the words with it.
        Assert.Equal(1, erased);
        Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM call_message"));
        Assert.NotNull(await store.GetAsync("c1", Token));
    }
}
