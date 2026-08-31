using AgentCore.Application.Calls;
using AgentCore.Application.Calls.Memory;
using Xunit;

namespace AgentCore.Application.Tests.Calls;

/// <summary>Store 0, kept in this process.</summary>
public sealed class InMemoryCallStoreTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CreateAsync_TheSameIdTwice_IsOneCall()
    {
        // Arrange
        InMemoryCallStore store = new();

        // Act
        var first = await store.CreateAsync("c1", Token);
        var second = await store.CreateAsync("c1", Token);

        // Assert
        Assert.Equal(first.CallId, second.CallId);
        Assert.Equal(first.CreatedAt, second.CreatedAt);
    }

    [Fact]
    public async Task GetAsync_ACallThatWasNeverMade_IsNull()
    {
        // Arrange
        InMemoryCallStore store = new();

        // Act
        var found = await store.GetAsync("missing", Token);

        // Assert
        Assert.Null(found);
    }

    [Fact]
    public async Task RenameAsync_ACall_ChangesOnlyItsTitle()
    {
        // Arrange
        InMemoryCallStore store = new();
        await store.CreateAsync("c1", Token);

        // Act
        await store.RenameAsync("c1", "A squeaky belt", Token);

        // Assert
        var found = await store.GetAsync("c1", Token);
        Assert.Equal("A squeaky belt", found!.Title);
        Assert.Equal(CallStatus.Regular, found.Status);
    }

    [Fact]
    public async Task ListAsync_APrincipalWithNoCalls_IsEmptyAndHasNoCursor()
    {
        // Arrange
        InMemoryCallStore store = new();
        await store.CreateAsync("c1", Token);

        // Act
        var page = await store.ListAsync("nobody", after: null, limit: 10, cancellationToken: Token);

        // Assert
        Assert.Empty(page.Calls);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task ListAsync_AnotherPrincipalsCall_IsNotReturned()
    {
        // Arrange
        InMemoryCallStore store = new();
        await store.CreateAsync("mine", Token);
        await store.CreateAsync("theirs", Token);
        await store.AttachPrincipalAsync("mine", "person-a", "caller", Token);
        await store.AttachPrincipalAsync("theirs", "person-b", "caller", Token);

        // Act
        var page = await store.ListAsync("person-a", after: null, limit: 10, cancellationToken: Token);

        // Assert
        Assert.Equal(["mine"], page.Calls.Select(call => call.CallId));
    }

    [Fact]
    public async Task ListAsync_TwoKeysOnOneCall_FindsItByEither()
    {
        // Arrange
        InMemoryCallStore store = new();
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

    [Fact]
    public async Task AttachPrincipalAsync_TheSamePairTwice_IsOneAttachment()
    {
        // Arrange
        InMemoryCallStore store = new();
        await store.CreateAsync("c1", Token);

        // Act
        await store.AttachPrincipalAsync("c1", "person-a", "caller", Token);
        await store.AttachPrincipalAsync("c1", "person-a", "caller", Token);

        // Assert
        var page = await store.ListAsync("person-a", after: null, limit: 10, cancellationToken: Token);
        Assert.Single(page.Calls);
    }

    [Fact]
    public async Task DetachPrincipalAsync_TheOnlyKey_LeavesTheCallUnlisted()
    {
        // Arrange
        InMemoryCallStore store = new();
        await store.CreateAsync("c1", Token);
        await store.AttachPrincipalAsync("c1", "person-a", "caller", Token);

        // Act
        await store.DetachPrincipalAsync("c1", "person-a", Token);

        // Assert
        Assert.Empty((await store.ListAsync("person-a", after: null, limit: 10, cancellationToken: Token)).Calls);
        Assert.NotNull(await store.GetAsync("c1", Token));
    }

    [Fact]
    public async Task ListAsync_ArchivedCalls_AreOutOfARegularListing()
    {
        // Arrange
        InMemoryCallStore store = new();
        await store.CreateAsync("c1", Token);
        await store.AttachPrincipalAsync("c1", "person-a", "caller", Token);
        await store.SetStatusAsync("c1", CallStatus.Archived, Token);

        // Act
        var regular = await store.ListAsync("person-a", after: null, limit: 10, CallStatus.Regular, Token);
        var everything = await store.ListAsync("person-a", after: null, limit: 10, cancellationToken: Token);

        // Assert
        Assert.Empty(regular.Calls);
        Assert.Single(everything.Calls);
    }

    [Fact]
    public async Task ListAsync_MoreCallsThanTheLimit_PagesWithNoGapAndNoRepeat()
    {
        // Arrange
        InMemoryCallStore store = new();
        for (var i = 0; i < 5; i++)
        {
            await store.CreateAsync($"c{i}", Token);
            await store.AttachPrincipalAsync($"c{i}", "person-a", "caller", Token);
        }

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
        Assert.Equal(5, seen.Count);
        Assert.Equal(5, seen.Distinct().Count());
    }

    [Fact]
    public async Task SetExternalIdAsync_AConsumersOwnId_IsReadBack()
    {
        // Arrange
        InMemoryCallStore store = new();
        await store.CreateAsync("c1", Token);

        // Act
        await store.SetExternalIdAsync("c1", "crm-77", Token);

        // Assert
        Assert.Equal("crm-77", (await store.GetAsync("c1", Token))!.ExternalId);
    }

    [Fact]
    public async Task SetExternalIdAsync_Null_ClearsTheId()
    {
        // Arrange
        InMemoryCallStore store = new();
        await store.CreateAsync("c1", Token);
        await store.SetExternalIdAsync("c1", "crm-77", Token);

        // Act
        await store.SetExternalIdAsync("c1", null, Token);

        // Assert
        Assert.Null((await store.GetAsync("c1", Token))!.ExternalId);
    }

    [Fact]
    public async Task DeleteAsync_ACall_TakesItsAttachmentsWithIt()
    {
        // Arrange
        InMemoryCallStore store = new();
        await store.CreateAsync("c1", Token);
        await store.AttachPrincipalAsync("c1", "person-a", "caller", Token);

        // Act
        await store.DeleteAsync("c1", Token);

        // Assert
        Assert.Null(await store.GetAsync("c1", Token));
        Assert.Empty((await store.ListAsync("person-a", after: null, limit: 10, cancellationToken: Token)).Calls);
    }
}
