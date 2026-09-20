using AgentCore.Application.Conversation;
using AgentCore.Application.Transcript;
using AgentCore.Infrastructure.Conversation.Postgres;
using AgentCore.Infrastructure.Tests.Database.Postgres;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Npgsql;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Conversation.Postgres;

/// <summary>
/// What an edit does to store 1 in PostgreSQL: which rows go, which stay, and what the names the
/// caller knows its messages by are good for.
/// </summary>
public sealed class PostgresConversationStoreTruncateTests : PostgresDatabaseTest
{
    /// <inheritdoc />
    protected override bool Migrated => true;

    private async Task<PostgresConversationStore> OpenAsync()
    {
        PostgresConversationStore store = new(DataSource);
        await store.CreateAsync("C1", Token);
        return store;
    }

    [PostgresFact]
    public async Task Truncate_TakesTheNamedOrdinalAndEverythingAfterIt()
    {
        var store = await OpenAsync();
        await store.AppendAsync(
            "C1",
            [
                new ConversationMessageDraft(0, new ChatMessage(ChatRole.User, "q1"), "m0"),
                new ConversationMessageDraft(0, new ChatMessage(ChatRole.Assistant, "a1"), "m1"),
                new ConversationMessageDraft(1, new ChatMessage(ChatRole.User, "q2"), "m2"),
                new ConversationMessageDraft(1, new ChatMessage(ChatRole.Assistant, "a2"), "m3"),
            ],
            state: null,
            Token);

        var went = await store.TruncateAsync("C1", 2, Token);

        Assert.Equal(new ConversationCut(2, new WithdrawnTurns(1, 1)), went);
        var rows = await store.ReadAllAsync("C1", Token);
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
            "C1",
            [
                new ConversationMessageDraft(0, new ChatMessage(ChatRole.User, "q1"), "m0"),
                new ConversationMessageDraft(0, new ChatMessage(ChatRole.Assistant, "a1"), "m1"),
                new ConversationMessageDraft(1, new ChatMessage(ChatRole.User, "q2"), "m2"),
            ],
            state: null,
            Token);
        await store.TruncateAsync("C1", 2, Token);

        await store.AppendAsync(
            "C1",
            [new ConversationMessageDraft(2, new ChatMessage(ChatRole.User, "q2 again"), "m3")],
            state: null,
            Token);

        var rows = await store.ReadAllAsync("C1", Token);
        Assert.Equal([0, 1, 3], rows.Select(row => row.Ordinal));
        Assert.Equal([0, 0, 2], rows.Select(row => row.TurnIndex));
    }

    [PostgresFact]
    public async Task AMessageName_RoundTripsAndIsUniqueWithinTheConversation()
    {
        var store = await OpenAsync();
        await store.AppendAsync(
            "C1",
            [new ConversationMessageDraft(0, new ChatMessage(ChatRole.User, "q1"), "m0")],
            state: null,
            Token);

        Assert.Equal("m0", (await store.ReadAllAsync("C1", Token))[0].MessageId);

        var clash = await Record.ExceptionAsync(
            () => store.AppendAsync(
                "C1",
                [new ConversationMessageDraft(0, new ChatMessage(ChatRole.User, "q2"), "m0")],
                state: null,
                Token).AsTask());

        Assert.Equal("23505", Assert.IsType<PostgresException>(clash).SqlState);
    }

    [PostgresFact]
    public async Task Truncate_LeavesTheStateAndTheConversationRowAlone()
    {
        // An edit withdraws words. It does not withdraw how far the conversation has got — the marks in the
        // state are what stop the next turn standing where a deleted row stood.
        var store = await OpenAsync();
        await store.AppendAsync(
            "C1",
            [new ConversationMessageDraft(0, new ChatMessage(ChatRole.User, "q1"), "m0")],
            new ConversationSessionState { Stage = "collecting", NextTurnIndex = 1 },
            Token);

        await store.TruncateAsync("C1", 0, Token);

        var record = await store.GetAsync("C1", Token);
        Assert.Equal("collecting", record?.State?.Stage);
        Assert.Equal(1, record?.NextOrdinal);
        Assert.Equal(1, record?.State?.NextTurnIndex);
        Assert.Empty(await store.ReadAllAsync("C1", Token));
    }
}
