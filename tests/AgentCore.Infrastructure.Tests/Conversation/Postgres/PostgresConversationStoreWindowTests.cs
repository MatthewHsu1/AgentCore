using AgentCore.Application.Transcript;
using AgentCore.Infrastructure.Conversation.Postgres;
using AgentCore.Infrastructure.Tests.Database.Postgres;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Conversation.Postgres;

/// <summary>Reading store 1 in PostgreSQL a window of turns at a time: newest turns first, every turn whole.</summary>
public sealed class PostgresConversationStoreWindowTests : PostgresDatabaseTest
{
    /// <inheritdoc />
    protected override bool Migrated => true;

    /// <summary>Four turns. Turn 2 answers through a tool, so it holds four rows rather than two.</summary>
    private async Task<PostgresConversationStore> FourTurnsAsync()
    {
        PostgresConversationStore store = new(DataSource);
        await store.CreateAsync("C1", Token);
        await store.AppendAsync(
            "C1",
            [
                new ConversationMessageDraft(0, new ChatMessage(ChatRole.User, "q0"), "m0"),
                new ConversationMessageDraft(0, new ChatMessage(ChatRole.Assistant, "a0"), "m1"),
                new ConversationMessageDraft(1, new ChatMessage(ChatRole.User, "q1"), "m2"),
                new ConversationMessageDraft(1, new ChatMessage(ChatRole.Assistant, "a1"), "m3"),
                new ConversationMessageDraft(2, new ChatMessage(ChatRole.User, "q2"), "m4"),
                new ConversationMessageDraft(2, new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call", "lookup")]), "m5"),
                new ConversationMessageDraft(2, new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call", "found")]), "m6"),
                new ConversationMessageDraft(2, new ChatMessage(ChatRole.Assistant, "a2"), "m7"),
                new ConversationMessageDraft(3, new ChatMessage(ChatRole.User, "q3"), "m8"),
                new ConversationMessageDraft(3, new ChatMessage(ChatRole.Assistant, "a3"), "m9"),
            ],
            state: null,
            Token);
        return store;
    }

    [PostgresFact]
    public async Task ReadWindow_NoStart_ReadsTheNewestTurnsWholeOldestRowFirst()
    {
        var store = await FourTurnsAsync();

        var rows = await store.ReadWindowAsync("C1", new TranscriptWindow(null, 2), Token);

        Assert.Equal(["m4", "m5", "m6", "m7", "m8", "m9"], rows.Select(row => row.MessageId));
    }

    [PostgresFact]
    public async Task ReadWindow_BeforeATurn_ReadsTheTurnsJustBelowIt()
    {
        var store = await FourTurnsAsync();

        var rows = await store.ReadWindowAsync("C1", new TranscriptWindow(2, 2), Token);

        Assert.Equal(["m0", "m1", "m2", "m3"], rows.Select(row => row.MessageId));
    }

    [PostgresFact]
    public async Task ReadWindow_PastTheStart_ReadsWhatIsLeft()
    {
        var store = await FourTurnsAsync();

        var rows = await store.ReadWindowAsync("C1", new TranscriptWindow(1, 5), Token);

        Assert.Equal(["m0", "m1"], rows.Select(row => row.MessageId));
    }

    [PostgresFact]
    public async Task ReadWindow_BeforeTheFirstTurn_ReadsNothing()
    {
        var store = await FourTurnsAsync();

        var rows = await store.ReadWindowAsync("C1", new TranscriptWindow(0, 5), Token);

        Assert.Empty(rows);
    }
}
