using System.Runtime.CompilerServices;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Runtime.Compaction;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Runtime;
using AgentCore.Application.Transcript;
using AgentCore.Domain.Audit;
using AgentCore.TestSupport;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Xunit;
using static AgentCore.Application.Tests.Transcript.ConversationSessionCompactionTestSupport;

namespace AgentCore.Application.Tests.Transcript;

/// <summary>
/// An edit against a conversation that carries a summary: cut above what the summary covers,
/// or reach under it and take the summary with the tail.
/// </summary>
#pragma warning disable MAAI001 // Compaction is evaluation-only in Microsoft.Agents.AI 1.21.0.
public sealed class ConversationSessionCompactionEditTests
{
    [Fact]
    public async Task ATurn_EditingAMessageUnderTheSummary_CutsTheStoreFirst_AndReadsBackOnlyWhatSurvived()
    {
        var store = new CountingReads(new InMemoryConversationStore());
        await store.CreateAsync("c1", TestContext.Current.CancellationToken);
        await SeedPlainTurnAsync(store, turnIndex: 0, "q0", "a0");
        await SeedPlainTurnAsync(store, turnIndex: 1, "q1", "a1");
        await SeedPlainTurnAsync(store, turnIndex: 2, "q2", "a2");

        var reply = new CapturingChatClient(_ => Task.CompletedTask, "a3", "a0, rewritten reply");
        RecordingObserver observer = new();
        var session = CreateSession(
            reply, store, conversationId: "c1", compaction: Summary(new ScriptedChatClient("the gist of it"), minimumPreservedGroups: 0), observer: observer);

        await session.RunTurnAsync("q3", TestContext.Current.CancellationToken);
        await session.FlushTranscriptAsync();

        // Turns 0 and 1 (q0/a0, q1/a1) are under the summary (D11: turn 2 stayed live as the newest
        // existing turn). "m-0-a" — turn 0's reply — sits inside that span.
        Assert.Single(await store.ReadForSessionAsync("c1", TestContext.Current.CancellationToken), row => row.CoversUpTo == 3);
        store.RowsRead.Clear();

        await session.RunTurnAtOriginAsync(
            "q0, rewritten",
            new ConversationTurnOrigin("caller-2", "m-0-a") { NamesParent = true },
            TestContext.Current.CancellationToken);
        await session.FlushTranscriptAsync();

        // The session's view is the raw rows again: the cut row's parent and the rewritten turn,
        // nothing from the dropped summary and nothing that was cut.
        var view = session.Compiled.History.Read(session.AgentSession!).Select(message => message.Text).ToList();
        Assert.Equal(["q0", "a0", "q0, rewritten", "a0, rewritten reply"], view);
        Assert.Equal(view, (await store.ReadForSessionAsync("c1", TestContext.Current.CancellationToken)).Select(row => row.Content.Text));
        Assert.Null(session.Compiled.History.Summary(session.AgentSession!));

        // The store handed the session the two rows the cut kept, not the eight it held before the
        // cut; the last read is this test's own.
        Assert.Equal([2, 4], store.RowsRead);

        // Turns 1 through 3 (q1/a1, q2/a2, q3/a3) went, and the trail says so even though the session
        // never held turn 1's rows.
        var superseded = Assert.Single(observer.Events, raised => raised.Kind == ConversationEventKind.TurnSuperseded);
        Assert.Equal("1", superseded.Payload![AuditPayloadKeys.WithdrewFromTurnIndex]);
        Assert.Equal("3", superseded.Payload[AuditPayloadKeys.WithdrewThroughTurnIndex]);
    }

    [Fact]
    public async Task ATurn_EditingAMessageTheStoreDoesNotHold_CutsNothing_AndKeepsTheSummary()
    {
        var store = new InMemoryConversationStore();
        await store.CreateAsync("c1", TestContext.Current.CancellationToken);
        await SeedPlainTurnAsync(store, turnIndex: 0, "q0", "a0");
        await SeedPlainTurnAsync(store, turnIndex: 1, "q1", "a1");
        await SeedPlainTurnAsync(store, turnIndex: 2, "q2", "a2");

        var reply = new CapturingChatClient(_ => Task.CompletedTask, "a3", "a4");
        RecordingObserver observer = new();
        var session = CreateSession(
            reply, store, conversationId: "c1", compaction: Summary(new ScriptedChatClient("the gist of it"), minimumPreservedGroups: 0), observer: observer);

        await session.RunTurnAsync("q3", TestContext.Current.CancellationToken);
        await session.FlushTranscriptAsync();

        await session.RunTurnAtOriginAsync(
            "q4",
            new ConversationTurnOrigin("caller-2", "nobody") { NamesParent = true },
            TestContext.Current.CancellationToken);
        await session.FlushTranscriptAsync();

        // Nothing went: the summary stands, and the turn compacts again over it (D11: q3/a3 was the
        // newest existing turn, so it stays live and the summary now covers everything before it).
        Assert.Equal(["assistant:[Summary]\nthe gist of it", "user:q3", "assistant:a3", "user:q4"], reply.Requests[1]);
        Assert.Single(await store.ReadForSessionAsync("c1", TestContext.Current.CancellationToken), row => row.CoversUpTo == 5);
        Assert.Equal(10, (await store.ReadAllAsync("c1", TestContext.Current.CancellationToken)).Count);
        Assert.DoesNotContain(observer.Events, raised => raised.Kind == ConversationEventKind.TurnSuperseded);
    }

    [Fact]
    public async Task ATurn_EditingAMessageAboveTheSummary_CutsTheTailAndKeepsTheSummary()
    {
        var store = new InMemoryConversationStore();
        await store.CreateAsync("c1", TestContext.Current.CancellationToken);
        await SeedPlainTurnAsync(store, turnIndex: 0, "q0", "a0");
        await SeedPlainTurnAsync(store, turnIndex: 1, "q1", "a1");
        await SeedPlainTurnAsync(store, turnIndex: 2, "q2", "a2");

        var reply = new CapturingChatClient(_ => Task.CompletedTask, "a3", "a3, rewritten reply");
        var session = CreateSession(reply, store, conversationId: "c1", compaction: Summary(new ScriptedChatClient("the gist of it"), minimumPreservedGroups: 0));

        await session.RunTurnAsync("q3", TestContext.Current.CancellationToken);
        await session.FlushTranscriptAsync();

        // The edit hangs off a2 (ordinal 5), above what the summary covers (3) and below the summary's
        // own ordinal (6). The cut takes q3/a3 (7, 8) and leaves the summary standing.
        await session.RunTurnAtOriginAsync(
            "q3, rewritten",
            new ConversationTurnOrigin("caller-2", "m-2-a") { NamesParent = true },
            TestContext.Current.CancellationToken);
        await session.FlushTranscriptAsync();

        Assert.Equal(
            ["assistant:[Summary]\nthe gist of it", "user:q2", "assistant:a2", "user:q3, rewritten"],
            reply.Requests[1]);
        var forSession = await store.ReadForSessionAsync("c1", TestContext.Current.CancellationToken);
        Assert.Single(forSession, row => row.CoversUpTo == 3);
        Assert.Equal([4, 5, 6, 9, 10], forSession.Select(row => row.Ordinal));
    }

    /// <summary>Notes how many rows each read handed back.</summary>
    private sealed class CountingReads(IConversationStore inner) : DelegatingConversationStore(inner)
    {
        public List<int> RowsRead { get; } = [];

        public override async ValueTask<IReadOnlyList<ConversationMessage>> ReadForSessionAsync(string conversationId, CancellationToken cancellationToken = default)
            => Note(await base.ReadForSessionAsync(conversationId, cancellationToken));

        private IReadOnlyList<ConversationMessage> Note(IReadOnlyList<ConversationMessage> rows)
        {
            RowsRead.Add(rows.Count);
            return rows;
        }
    }
}
#pragma warning restore MAAI001
