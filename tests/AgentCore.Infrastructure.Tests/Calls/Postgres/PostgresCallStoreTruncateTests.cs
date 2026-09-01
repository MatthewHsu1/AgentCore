using AgentCore.Application.Calls;
using AgentCore.Application.Transcript;
using AgentCore.Infrastructure.Calls.Postgres;
using AgentCore.Infrastructure.Tests.Database.Postgres;
using Microsoft.Extensions.AI;
using Npgsql;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Calls.Postgres;

/// <summary>
/// What an edit does to store 1 in PostgreSQL: which rows go, which stay, and what the names the
/// caller knows its messages by are good for.
/// </summary>
public sealed class PostgresCallStoreTruncateTests : PostgresDatabaseTest
{
    /// <inheritdoc />
    protected override bool Migrated => true;

    private async Task<PostgresCallStore> OpenAsync()
    {
        PostgresCallStore store = new(DataSource);
        await store.CreateAsync("C1", Token);
        return store;
    }

    [PostgresFact]
    public async Task Truncate_TakesTheNamedOrdinalAndEverythingAfterIt()
    {
        var store = await OpenAsync();
        await store.AppendAsync(
            [
                new CallMessage("C1", 0, 0, new ChatMessage(ChatRole.User, "q1"), "m0"),
                new CallMessage("C1", 1, 0, new ChatMessage(ChatRole.Assistant, "a1"), "m1"),
                new CallMessage("C1", 2, 1, new ChatMessage(ChatRole.User, "q2"), "m2"),
                new CallMessage("C1", 3, 1, new ChatMessage(ChatRole.Assistant, "a2"), "m3"),
            ],
            state: null,
            Token);

        var went = await store.TruncateAsync("C1", 2, Token);

        Assert.Equal(2, went);
        var rows = await store.ReadAsync("C1", Token);
        Assert.Equal([0, 1], rows.Select(row => row.Ordinal));
    }

    [PostgresFact]
    public async Task Truncate_LeavesTheNextAppendFreeToClimbPastTheGap()
    {
        // The ordinals a truncation takes are never issued again, so an append after one leaves a
        // hole. Nothing may object to that: store 3 keeps audit rows against the turns that stood in
        // the gap, and reissuing a number would put two turns in one place in the chain.
        var store = await OpenAsync();
        await store.AppendAsync(
            [
                new CallMessage("C1", 0, 0, new ChatMessage(ChatRole.User, "q1"), "m0"),
                new CallMessage("C1", 1, 0, new ChatMessage(ChatRole.Assistant, "a1"), "m1"),
                new CallMessage("C1", 2, 1, new ChatMessage(ChatRole.User, "q2"), "m2"),
            ],
            state: null,
            Token);
        await store.TruncateAsync("C1", 2, Token);

        await store.AppendAsync(
            [new CallMessage("C1", 3, 2, new ChatMessage(ChatRole.User, "q2 again"), "m3")],
            state: null,
            Token);

        var rows = await store.ReadAsync("C1", Token);
        Assert.Equal([0, 1, 3], rows.Select(row => row.Ordinal));
        Assert.Equal([0, 0, 2], rows.Select(row => row.TurnIndex));
    }

    [PostgresFact]
    public async Task AMessageName_RoundTripsAndIsUniqueWithinTheCall()
    {
        var store = await OpenAsync();
        await store.AppendAsync(
            [new CallMessage("C1", 0, 0, new ChatMessage(ChatRole.User, "q1"), "m0")],
            state: null,
            Token);

        Assert.Equal("m0", (await store.ReadAsync("C1", Token))[0].MessageId);

        var clash = await Record.ExceptionAsync(
            () => store.AppendAsync(
                [new CallMessage("C1", 1, 0, new ChatMessage(ChatRole.User, "q2"), "m0")],
                state: null,
                Token).AsTask());

        Assert.Equal("23505", Assert.IsType<PostgresException>(clash).SqlState);
    }

    [PostgresFact]
    public async Task Truncate_LeavesTheStateAndTheCallRowAlone()
    {
        // An edit withdraws words. It does not withdraw how far the call has got — the marks in the
        // state are what stop the next turn standing where a deleted row stood.
        var store = await OpenAsync();
        await store.AppendAsync(
            [new CallMessage("C1", 0, 0, new ChatMessage(ChatRole.User, "q1"), "m0")],
            new CallSessionState { Stage = "collecting", NextOrdinal = 1, NextTurnIndex = 1 },
            Token);

        await store.TruncateAsync("C1", 0, Token);

        var record = await store.GetAsync("C1", Token);
        Assert.Equal("collecting", record?.State?.Stage);
        Assert.Equal(1, record?.State?.NextOrdinal);
        Assert.Equal(1, record?.State?.NextTurnIndex);
        Assert.Empty(await store.ReadAsync("C1", Token));
    }
}
