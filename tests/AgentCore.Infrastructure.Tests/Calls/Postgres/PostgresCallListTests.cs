using AgentCore.Application.Calls;
using AgentCore.Infrastructure.Calls.Postgres;
using AgentCore.Infrastructure.Tests.Database.Postgres;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Calls.Postgres;

/// <summary>The listing behind a thread sidebar.</summary>
public sealed class PostgresCallListTests : PostgresDatabaseTest
{
    /// <inheritdoc />
    protected override bool Migrated => true;

    [PostgresFact]
    public async Task ListAsync_APrincipalWithNoCalls_IsEmptyAndHasNoCursor()
    {
        // Arrange
        PostgresCallStore store = new(DataSource);
        await store.CreateAsync("c1", Token);

        // Act
        var page = await store.ListAsync("nobody", after: null, limit: 10, cancellationToken: Token);

        // Assert
        Assert.Empty(page.Calls);
        Assert.Null(page.NextCursor);
    }

    [PostgresFact]
    public async Task ListAsync_AnotherPrincipalsCall_IsNotReturned()
    {
        // Arrange
        PostgresCallStore store = new(DataSource);
        await Claim(store, "mine", "person-a");
        await Claim(store, "theirs", "person-b");

        // Act
        var page = await store.ListAsync("person-a", after: null, limit: 10, cancellationToken: Token);

        // Assert
        Assert.Equal(["mine"], page.Calls.Select(call => call.CallId));
    }

    [PostgresFact]
    public async Task ListAsync_ACallWithTwoKeys_IsFoundByEither()
    {
        // Arrange
        PostgresCallStore store = new(DataSource);
        await store.CreateAsync("c1", Token);
        await store.AttachPrincipalAsync("c1", "tenant-a", "tenant", Token);
        await store.AttachPrincipalAsync("c1", "person-a", "caller", Token);

        // Act
        var byTenant = await store.ListAsync("tenant-a", after: null, limit: 10, cancellationToken: Token);
        var byPerson = await store.ListAsync("person-a", after: null, limit: 10, cancellationToken: Token);

        // Assert
        Assert.Single(byTenant.Calls);
        Assert.Single(byPerson.Calls);
    }

    [PostgresFact]
    public async Task ListAsync_MostRecentlySpokenFirst_IsTheOrder()
    {
        // Arrange
        PostgresCallStore store = new(DataSource);
        await Claim(store, "old", "person-a");
        await Claim(store, "new", "person-a");
        await Spoke("old", "-2 hours");
        await Spoke("new", "-1 minute");

        // Act
        var page = await store.ListAsync("person-a", after: null, limit: 10, cancellationToken: Token);

        // Assert
        Assert.Equal(["new", "old"], page.Calls.Select(call => call.CallId));
    }

    [PostgresFact]
    public async Task ListAsync_ACallWithNoMessages_IsListedByItsCreationTime()
    {
        // Arrange
        PostgresCallStore store = new(DataSource);
        await Claim(store, "spoken", "person-a");
        await Spoke("spoken", "-2 hours");
        await Claim(store, "silent", "person-a");

        // Act
        var page = await store.ListAsync("person-a", after: null, limit: 10, cancellationToken: Token);

        // Assert
        Assert.Equal(["silent", "spoken"], page.Calls.Select(call => call.CallId));
    }

    [PostgresFact]
    public async Task ListAsync_ArchivedByDefault_IsStillListed()
    {
        // Arrange
        PostgresCallStore store = new(DataSource);
        await Claim(store, "c1", "person-a");
        await store.SetStatusAsync("c1", CallStatus.Archived, Token);

        // Act
        var page = await store.ListAsync("person-a", after: null, limit: 10, cancellationToken: Token);

        // Assert
        Assert.Single(page.Calls);
        Assert.Equal(CallStatus.Archived, page.Calls[0].Status);
    }

    [PostgresFact]
    public async Task ListAsync_NarrowedToRegular_LeavesArchivedOut()
    {
        // Arrange
        PostgresCallStore store = new(DataSource);
        await Claim(store, "kept", "person-a");
        await Claim(store, "filed", "person-a");
        await store.SetStatusAsync("filed", CallStatus.Archived, Token);

        // Act
        var page = await store.ListAsync("person-a", after: null, limit: 10, CallStatus.Regular, Token);

        // Assert
        Assert.Equal(["kept"], page.Calls.Select(call => call.CallId));
    }

    [PostgresFact]
    public async Task ListAsync_PagedToTheEnd_ReturnsEveryCallExactlyOnce()
    {
        // Arrange
        PostgresCallStore store = new(DataSource);
        for (var i = 0; i < 25; i++)
        {
            await Claim(store, $"c{i:D2}", "person-a");
            await Spoke($"c{i:D2}", $"-{i} minutes");
        }

        // Act
        List<string> seen = [];
        string? cursor = null;
        do
        {
            var page = await store.ListAsync("person-a", cursor, limit: 7, cancellationToken: Token);
            seen.AddRange(page.Calls.Select(call => call.CallId));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        // Assert
        Assert.Equal(25, seen.Count);
        Assert.Equal(25, seen.Distinct().Count());
    }

    [PostgresFact]
    public async Task ListAsync_CallsSharingOneTimestamp_AreStillPagedWithoutLoss()
    {
        // Arrange
        PostgresCallStore store = new(DataSource);
        for (var i = 0; i < 6; i++)
        {
            await Claim(store, $"c{i}", "person-a");
        }

        await ExecuteAsync("UPDATE call SET created_at = timestamptz '2026-01-01 00:00:00+00'");

        // Act
        List<string> seen = [];
        string? cursor = null;
        do
        {
            var page = await store.ListAsync("person-a", cursor, limit: 2, cancellationToken: Token);
            seen.AddRange(page.Calls.Select(call => call.CallId));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        // Assert
        Assert.Equal(6, seen.Count);
        Assert.Equal(6, seen.Distinct().Count());
    }

    [PostgresFact]
    public async Task ListAsync_AnExactlyFullLastPage_EndsOnTheFollowingEmptyOne()
    {
        // Arrange
        PostgresCallStore store = new(DataSource);
        await Claim(store, "c1", "person-a");
        await Claim(store, "c2", "person-a");

        // Act
        var first = await store.ListAsync("person-a", after: null, limit: 2, cancellationToken: Token);
        var second = await store.ListAsync("person-a", first.NextCursor, limit: 2, cancellationToken: Token);

        // Assert
        Assert.NotNull(first.NextCursor);
        Assert.Empty(second.Calls);
        Assert.Null(second.NextCursor);
    }

    [PostgresFact]
    public async Task ListAsync_ACursorThatIsNotOne_ServesTheFirstPage()
    {
        // Arrange
        PostgresCallStore store = new(DataSource);
        await Claim(store, "c1", "person-a");

        // Act
        var page = await store.ListAsync("person-a", "not-a-cursor", limit: 10, cancellationToken: Token);

        // Assert
        Assert.Single(page.Calls);
    }

    private static async Task Claim(PostgresCallStore store, string callId, string principalKey)
    {
        await store.CreateAsync(callId, Token);
        await store.AttachPrincipalAsync(callId, principalKey, "caller", Token);
    }

    private Task Spoke(string callId, string ago) =>
        ExecuteAsync(
            $$"""
              INSERT INTO call_message (call_id, ordinal, turn_index, role, content, message_id, created_at, updated_at)
              VALUES ('{{callId}}', 1, 0, 'user', '{}'::jsonb, 'm1', now(), now() + interval '{{ago}}')
              """);
}
