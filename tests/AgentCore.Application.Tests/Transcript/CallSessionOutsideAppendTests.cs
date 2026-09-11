using AgentCore.Application.Calls.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Runtime;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;
using Xunit;
using static AgentCore.Application.Tests.Transcript.CallSessionResumeTestSupport;

namespace AgentCore.Application.Tests.Transcript;

/// <summary>
/// A turn sees a row a host appended from outside any turn — after a human handoff ends, say,
/// possibly from another machine — and its own new rows still continue from store 0's counter.
/// </summary>
#pragma warning disable CA1859 // store is typed as ICallStore throughout: AppendMessageAsync(callId, message, ct)
                               // is a default interface method, and only resolves through the interface type.
public sealed class CallSessionOutsideAppendTests
{
    [Fact]
    public async Task SecondTurn_SeesTheOutsideMessagesBetweenTheTurns_AndKeepsTheirProperties()
    {
        ICallStore store = new InMemoryCallStore();
        var (_, client) = await RunTwoTurnsWithAnOutsideHandoffAsync(store);

        var secondRequest = client.Requests[1];

        // Both outside rows land between turn 1's words and turn 2's own question, in the order the
        // host wrote them.
        Assert.Equal(
            ["my name is Dana", "hi Dana", "Dana joined", "human phase", "are you still there?"],
            secondRequest.Select(message => message.Text));

        var joined = Assert.Single(secondRequest, message => message.Text == "Dana joined");
        Assert.Equal("human-agent", joined.AdditionalProperties?["speaker"]?.ToString());
    }

    [Fact]
    public async Task AfterTheSecondTurn_TheStoresOrdinalsAreDenseAndUnique()
    {
        ICallStore store = new InMemoryCallStore();
        var (session, _) = await RunTwoTurnsWithAnOutsideHandoffAsync(store);

        var rows = await store.ReadAsync(session.CallId, TestContext.Current.CancellationToken);
        var ordinals = rows.Select(row => row.Ordinal).ToArray();

        for (var index = 1; index < ordinals.Length; index++)
        {
            Assert.True(ordinals[index] > ordinals[index - 1]);
        }

        // No truncate ran in this scenario, so the counter and the row count agree exactly.
        var record = await store.GetAsync(session.CallId, TestContext.Current.CancellationToken);
        Assert.Equal(rows.Count, record?.NextOrdinal);
    }

    [Fact]
    public async Task ASecondSessionOverTheSameStore_SeesTheOutsideMessageAndContinuesTheCounter()
    {
        // Two CallSession instances of one call, standing in for two machines: nothing but the store
        // passes between them.
        ICallStore store = new InMemoryCallStore();

        SequencedChatClient clientA = new("hi Dana");
        var sessionA = CreateSession(OneAgentYaml, clientA, store);
        await sessionA.RunTurnAsync("my name is Dana", TestContext.Current.CancellationToken);
        await sessionA.FlushTranscriptAsync();

        await store.AppendMessageAsync(
            sessionA.CallId,
            new ChatMessage(ChatRole.Assistant, "Dana joined")
            {
                AdditionalProperties = new() { ["speaker"] = "human-agent" },
            },
            TestContext.Current.CancellationToken);

        SequencedChatClient clientB = new("welcome back");
        var sessionB = CreateSession(OneAgentYaml, clientB, store, sessionA.CallId);

        await sessionB.RunTurnAsync("still there?", TestContext.Current.CancellationToken);
        await sessionB.FlushTranscriptAsync();

        Assert.Equal(
            ["my name is Dana", "hi Dana", "Dana joined", "still there?"],
            clientB.Requests[0].Select(message => message.Text));

        var rows = await store.ReadAsync(sessionA.CallId, TestContext.Current.CancellationToken);
        Assert.Equal([0, 1, 2, 3, 4], rows.Select(row => row.Ordinal));

        var record = await store.GetAsync(sessionA.CallId, TestContext.Current.CancellationToken);
        Assert.Equal(rows.Count, record?.NextOrdinal);
    }

    [Fact]
    public async Task AnEditAfterAnOutsideAppend_CutsTheOutsideRowWithTheReplyItReplaces()
    {
        ICallStore store = new InMemoryCallStore();
        SequencedChatClient client = new("first reply", "second reply");
        var session = CreateSession(OneAgentYaml, client, store);

        await session.RunTurnAtOriginAsync(
            "first question",
            new CallTurnOrigin("m1", null) { NamesParent = true },
            TestContext.Current.CancellationToken);
        await session.FlushTranscriptAsync();

        await store.AppendMessageAsync(
            session.CallId,
            new ChatMessage(ChatRole.Assistant, "Dana joined")
            {
                AdditionalProperties = new() { ["speaker"] = "human-agent" },
            },
            TestContext.Current.CancellationToken);

        // Names m1 as the parent, so the edit must see the outside row that now sits after it in
        // order to cut it along with the reply it replaces.
        await session.RunTurnAtOriginAsync(
            "first question, rewritten",
            new CallTurnOrigin("m2", "m1") { NamesParent = true },
            TestContext.Current.CancellationToken);
        await session.FlushTranscriptAsync();

        var rows = await store.ReadAsync(session.CallId, TestContext.Current.CancellationToken);

        Assert.Equal(3, rows.Count);
        Assert.Equal("m1", rows[0].MessageId);
        Assert.Equal("m2", rows[1].MessageId);
        Assert.Equal(
            ["first question", "first question, rewritten", "second reply"],
            rows.Select(row => row.Content.Text));
    }

    [Fact]
    public async Task AfterOneTurn_TheStoredBlobCarriesTheTurnIndexAndTheCounterLivesOnTheRecord()
    {
        ICallStore store = new InMemoryCallStore();
        SequencedChatClient client = new("hi there");
        var session = CreateSession(OneAgentYaml, client, store);

        await session.RunTurnAsync("hello", TestContext.Current.CancellationToken);
        await session.FlushTranscriptAsync();

        var record = await store.GetAsync(session.CallId, TestContext.Current.CancellationToken);

        // One turn writes two rows (what the caller said, what it heard) and leaves the next turn at
        // index 1 — the mark the blob carries now that the ordinal has its own column on the record.
        Assert.Equal(1, record?.State?.NextTurnIndex);
        Assert.Equal(2, record?.NextOrdinal);
    }

    [Fact]
    public async Task SecondTurn_WithNoOutsideAppend_DoesNotReadTheWordsBack()
    {
        RecordingCallStore store = new();
        SequencedChatClient client = new("hi Dana", "welcome back");
        var session = CreateSession(OneAgentYaml, client, store);

        await session.RunTurnAsync("my name is Dana", TestContext.Current.CancellationToken);
        await session.FlushTranscriptAsync();
        await session.RunTurnAsync("are you still there?", TestContext.Current.CancellationToken);
        await session.FlushTranscriptAsync();

        // The counter store 0 hands back matches the one the session holds, so nobody wrote from
        // outside and there is nothing to read.
        Assert.Equal(0, store.Reads);
        Assert.Equal(
            ["my name is Dana", "hi Dana", "are you still there?"],
            client.Requests[1].Select(message => message.Text));
    }

    [Fact]
    public async Task SecondTurn_AfterAnOutsideAppend_ReadsTheWordsBackOnce()
    {
        RecordingCallStore store = new();
        await RunTwoTurnsWithAnOutsideHandoffAsync(store);

        Assert.Equal(1, store.Reads);
    }

    private static async Task<(CallSession Session, SequencedChatClient Client)> RunTwoTurnsWithAnOutsideHandoffAsync(
        ICallStore store)
    {
        SequencedChatClient client = new("hi Dana", "welcome back");
        var session = CreateSession(OneAgentYaml, client, store);

        await session.RunTurnAsync("my name is Dana", TestContext.Current.CancellationToken);
        await session.FlushTranscriptAsync();

        await store.AppendMessageAsync(
            session.CallId,
            new ChatMessage(ChatRole.Assistant, "Dana joined")
            {
                AdditionalProperties = new() { ["speaker"] = "human-agent" },
            },
            TestContext.Current.CancellationToken);
        await store.AppendMessageAsync(
            session.CallId,
            new ChatMessage(ChatRole.User, "human phase"),
            TestContext.Current.CancellationToken);

        await session.RunTurnAsync("are you still there?", TestContext.Current.CancellationToken);
        await session.FlushTranscriptAsync();

        return (session, client);
    }
}
#pragma warning restore CA1859
