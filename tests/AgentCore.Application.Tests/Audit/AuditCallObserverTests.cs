using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Audit;
using AgentCore.Application.Runtime;
using AgentCore.Domain.Audit;
using Xunit;
using Xunit.Sdk;

namespace AgentCore.Application.Tests.Audit;

/// <summary>
/// The one place that knows both vocabularies: a neutral fact of a call, and a row of the chain of D23.
/// </summary>
/// <remarks>
/// Two rules carry the chain. The six kinds the chain stores keep the identity the session gave them,
/// so <see cref="AuditEvent.EventId"/> is the session's <see cref="CallEvent.EventId"/> and never an
/// id this observer or the sink invented. The diagnostic kinds took no identity and write no row,
/// which is what keeps the chain free of rows nobody can name.
/// </remarks>
public sealed class AuditCallObserverTests
{
    private const string CallId = "call-1";

    private static readonly DateTimeOffset Moment = new(2026, 8, 15, 9, 30, 0, TimeSpan.Zero);

    /// <summary>The six kinds that are counted or logged or both, and stored nowhere.</summary>
    /// <remarks>
    /// All six, and not the four the chain's vocabulary happened to leave over when this table was
    /// written. Every entry here is the only observer-level proof that its kind writes no audit row,
    /// so a kind added to <see cref="CallEventKind"/> and not to this table is a kind whose silence
    /// nothing checks.
    /// </remarks>
    public static TheoryData<CallEventKind> DiagnosticKinds =>
    [
        CallEventKind.ModerationUnavailable,
        CallEventKind.ModerationClean,
        CallEventKind.EmptyReply,
        CallEventKind.ExtractionFailed,
        CallEventKind.TranscriptWriteFailed,
        CallEventKind.StateRestorePartial,
    ];

    /// <summary>Every kind, once, across the two tables above.</summary>
    /// <remarks>
    /// The tables are hand-written, and the point of each is that it is EXHAUSTIVE. Neither can say
    /// so alone: a missing entry just runs one theory case fewer and passes.
    /// </remarks>
    [Fact]
    public void TheTwoTables_BetweenThemNameEveryKind()
    {
        IEnumerable<CallEventKind> named =
        [
            .. DiagnosticKinds.Select(row => (CallEventKind)((ITheoryDataRow)row).GetData()[0]!),
            .. StoredKinds.Select(row => (CallEventKind)((ITheoryDataRow)row).GetData()[0]!),
        ];

        Assert.Equal([.. Enum.GetValues<CallEventKind>().Order()], [.. named.Order()]);
    }

    /// <summary>The six kinds the store keeps, beside the token each one writes.</summary>
    public static TheoryData<CallEventKind, AuditEventKind> StoredKinds =>
        new()
        {
            { CallEventKind.CallStarted, AuditEventKind.CallStarted },
            { CallEventKind.PromptFlagged, AuditEventKind.PromptFlagged },
            { CallEventKind.ToolFailed, AuditEventKind.ToolFailed },
            { CallEventKind.TurnCompleted, AuditEventKind.TurnCompleted },
            { CallEventKind.ReplyInterrupted, AuditEventKind.ReplyInterrupted },
            { CallEventKind.CallEnded, AuditEventKind.CallEnded },
        };

    [Theory]
    [MemberData(nameof(DiagnosticKinds))]
    public async Task AFactWithNoEventId_WritesNoRow(CallEventKind kind)
    {
        InMemoryAuditSink sink = new();
        AuditCallObserver observer = new(sink);

        // A diagnostic-only event leaves EventId null precisely because it takes no row.
        await observer.OnCallEventAsync(Event(kind), TestContext.Current.CancellationToken);

        Assert.Empty(sink.Events);
    }

    [Theory]
    [MemberData(nameof(StoredKinds))]
    public async Task AStoredFact_BecomesARowOfItsOwnKind(CallEventKind kind, AuditEventKind expected)
    {
        InMemoryAuditSink sink = new();
        AuditCallObserver observer = new(sink);

        await observer.OnCallEventAsync(
            Event(kind, eventId: Guid.CreateVersion7(), amends: Guid.CreateVersion7()),
            TestContext.Current.CancellationToken);

        var written = Assert.Single(sink.Events);
        Assert.Equal(expected, written.Kind);
    }

    [Fact]
    public async Task ItCopiesTheIdentityStraightThrough()
    {
        InMemoryAuditSink sink = new();
        AuditCallObserver observer = new(sink);
        var id = Guid.CreateVersion7();

        await observer.OnCallEventAsync(
            new CallEvent
            {
                CallId = CallId,
                Kind = CallEventKind.CallStarted,
                OccurredAt = DateTimeOffset.UnixEpoch,
                EventId = id,
            },
            CancellationToken.None);

        Assert.Equal(id, Assert.Single(sink.EventsOf(CallId)).EventId);
    }

    [Fact]
    public async Task ItCopiesTheAmendmentStraightThrough()
    {
        InMemoryAuditSink sink = new();
        AuditCallObserver observer = new(sink);
        var turn = Guid.CreateVersion7();
        var cut = Guid.CreateVersion7();

        await observer.OnCallEventAsync(
            new CallEvent
            {
                CallId = CallId,
                Kind = CallEventKind.ReplyInterrupted,
                OccurredAt = DateTimeOffset.UnixEpoch,
                EventId = cut,
                AmendsEventId = turn,
                TurnIndex = 0,
                Payload = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AuditPayloadKeys.UtteranceUntilInterruptSha256] = AuditHash.OfText("heard").Value,
                    [AuditPayloadKeys.DurationUntilInterruptMs] = "120",
                },
            },
            CancellationToken.None);

        var written = Assert.Single(sink.EventsOf(CallId));
        Assert.Equal(cut, written.EventId);
        Assert.Equal(turn, written.AmendsEventId);
    }

    [Fact]
    public async Task AFactThatCorrectsNothing_LeavesTheAmendmentUnset()
    {
        InMemoryAuditSink sink = new();
        AuditCallObserver observer = new(sink);

        await observer.OnCallEventAsync(
            Event(CallEventKind.TurnCompleted, eventId: Guid.CreateVersion7()),
            TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(sink.Events).AmendsEventId);
    }

    [Fact]
    public async Task TheRow_CarriesTheCallTheTurnAndTheMomentUnchanged()
    {
        InMemoryAuditSink sink = new();
        AuditCallObserver observer = new(sink);
        Dictionary<string, string> payload = new(StringComparer.Ordinal)
        {
            [AuditPayloadKeys.ReplyTextSha256] = AuditHash.OfText("hello there.").Value,
            [AuditPayloadKeys.StageBefore] = "greeting",
            [AuditPayloadKeys.StageAfter] = "triage",
        };

        await observer.OnCallEventAsync(
            Event(CallEventKind.TurnCompleted, eventId: Guid.CreateVersion7(), turnIndex: 5, payload: payload),
            TestContext.Current.CancellationToken);

        var written = Assert.Single(sink.Events);
        Assert.Equal(CallId, written.CallId);
        Assert.Equal(5, written.TurnIndex);
        Assert.Equal(Moment, written.OccurredAt);
        Assert.Equal(payload, written.Payload);
    }

    [Fact]
    public async Task AFactAboutTheCallItself_CarriesNoTurn()
    {
        InMemoryAuditSink sink = new();
        AuditCallObserver observer = new(sink);

        await observer.OnCallEventAsync(
            Event(CallEventKind.CallStarted, eventId: Guid.CreateVersion7()),
            TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(sink.Events).TurnIndex);
    }

    [Fact]
    public async Task ManyFacts_ReachTheSinkInTheOrderTheCallRaisedThem()
    {
        InMemoryAuditSink sink = new();
        AuditCallObserver observer = new(sink);
        var token = TestContext.Current.CancellationToken;

        await observer.OnCallEventAsync(Event(CallEventKind.CallStarted, eventId: Guid.CreateVersion7()), token);

        // The two diagnostic facts between them take no identity, so they write no row.
        await observer.OnCallEventAsync(Event(CallEventKind.ModerationClean), token);
        await observer.OnCallEventAsync(
            Event(CallEventKind.TurnCompleted, eventId: Guid.CreateVersion7(), turnIndex: 0), token);
        await observer.OnCallEventAsync(Event(CallEventKind.EmptyReply, turnIndex: 0), token);
        await observer.OnCallEventAsync(Event(CallEventKind.CallEnded, eventId: Guid.CreateVersion7()), token);

        Assert.Equal(
            [AuditEventKind.CallStarted, AuditEventKind.TurnCompleted, AuditEventKind.CallEnded],
            sink.EventsOf(CallId).Select(item => item.Kind).ToArray());
    }

    [Fact]
    public void NoSink_IsRefused() => Assert.Throws<ArgumentNullException>(() => new AuditCallObserver(null!));

    [Fact]
    public async Task NoEvent_IsRefused()
        => await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await new AuditCallObserver(new InMemoryAuditSink())
                .OnCallEventAsync(null!, TestContext.Current.CancellationToken));

    private static CallEvent Event(
        CallEventKind kind,
        Guid? eventId = null,
        int? turnIndex = null,
        Guid? amends = null,
        IReadOnlyDictionary<string, string>? payload = null) => new()
        {
            CallId = CallId,
            Kind = kind,
            OccurredAt = Moment,
            EventId = eventId,
            TurnIndex = turnIndex,
            AmendsEventId = amends,
            Payload = payload ?? RequiredPayload(kind),
        };

    /// <summary>
    /// The payload each kind must carry to be a legal event, per <see cref="AuditEventVocabulary"/>.
    /// </summary>
    /// <remarks>
    /// A sink refuses an event that is missing these, so a test that fabricates one has to supply
    /// them or it asserts about a row that could never be written.
    /// </remarks>
    private static Dictionary<string, string> RequiredPayload(CallEventKind kind) => kind switch
    {
        CallEventKind.ReplyInterrupted => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AuditPayloadKeys.UtteranceUntilInterruptSha256] = AuditHash.OfText("the belt ships").Value,
        },
        CallEventKind.PromptFlagged => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AuditPayloadKeys.ModerationCategories] = "harassment",
        },
        CallEventKind.CallEnded => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AuditPayloadKeys.EndReason] = CallEndReasons.ToToken(CallEndReason.CallerHungUp),
        },
        _ => new Dictionary<string, string>(StringComparer.Ordinal),
    };
}
