using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Audit;
using AgentCore.Application.Runtime;
using AgentCore.Domain.Audit;
using Xunit;

namespace AgentCore.Application.Tests.Audit;

/// <summary>
/// The one place that knows both vocabularies: a neutral fact of a call, and a row of the chain of D23.
/// </summary>
/// <remarks>
/// Two rules carry the chain. The six kinds the chain stores keep the number the session gave them, so
/// <see cref="AuditEvent.Sequence"/> is the session's ordinal and never a number this observer or the
/// sink invented. The four diagnostic kinds took no number and write no row, which is what keeps the
/// sequence gap-free and monotonic from zero, exactly as it was before the hook existed.
/// </remarks>
public sealed class AuditCallObserverTests
{
    private const string CallId = "call-1";

    private static readonly DateTimeOffset Moment = new(2026, 8, 15, 9, 30, 0, TimeSpan.Zero);

    /// <summary>The four kinds that are counted and logged and stored nowhere.</summary>
    public static TheoryData<CallEventKind> DiagnosticKinds =>
    [
        CallEventKind.ModerationUnavailable,
        CallEventKind.ModerationClean,
        CallEventKind.EmptyReply,
        CallEventKind.ExtractionFailed,
    ];

    /// <summary>The six kinds the chain stores, beside the token each one writes.</summary>
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
    public async Task AFactWithNoOrdinal_WritesNoRow(CallEventKind kind)
    {
        InMemoryAuditSink sink = new();
        AuditCallObserver observer = new(sink);

        // A diagnostic-only event leaves Ordinal null precisely so that it consumes no number.
        await observer.OnCallEventAsync(Event(kind), TestContext.Current.CancellationToken);

        Assert.Empty(sink.Events);
    }

    [Theory]
    [MemberData(nameof(StoredKinds))]
    public async Task AStoredFact_BecomesARowOfItsOwnKind(CallEventKind kind, AuditEventKind expected)
    {
        InMemoryAuditSink sink = new();
        AuditCallObserver observer = new(sink);

        await observer.OnCallEventAsync(Event(kind, ordinal: 0), TestContext.Current.CancellationToken);

        var written = Assert.Single(sink.Events);
        Assert.Equal(expected, written.Kind);
    }

    [Fact]
    public async Task TheOrdinalOfTheSession_IsTheSequenceOfTheRow()
    {
        InMemoryAuditSink sink = new();
        AuditCallObserver observer = new(sink);

        // The session allocates the number, not the sink: the sink answers long after the turn moved
        // on, so a number it allocated would reach nobody in time.
        await observer.OnCallEventAsync(
            Event(CallEventKind.TurnCompleted, ordinal: 7),
            TestContext.Current.CancellationToken);

        var written = Assert.Single(sink.Events);
        Assert.Equal(7, written.Sequence);
    }

    [Fact]
    public async Task AnAmendment_NamesTheSequenceItCorrects()
    {
        InMemoryAuditSink sink = new();
        AuditCallObserver observer = new(sink);

        // T23: the chain is append-only, so a barge-in is a second event that names the first.
        await observer.OnCallEventAsync(
            Event(CallEventKind.ReplyInterrupted, ordinal: 4, amends: 3),
            TestContext.Current.CancellationToken);

        var written = Assert.Single(sink.Events);
        Assert.Equal(4, written.Sequence);
        Assert.Equal(3, written.AmendsSequence);
    }

    [Fact]
    public async Task AFactThatCorrectsNothing_LeavesTheAmendmentUnset()
    {
        InMemoryAuditSink sink = new();
        AuditCallObserver observer = new(sink);

        await observer.OnCallEventAsync(
            Event(CallEventKind.TurnCompleted, ordinal: 1),
            TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(sink.Events).AmendsSequence);
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
            Event(CallEventKind.TurnCompleted, ordinal: 2, turnIndex: 5, payload: payload),
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
            Event(CallEventKind.CallStarted, ordinal: 0),
            TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(sink.Events).TurnIndex);
    }

    [Fact]
    public async Task ManyFacts_ReachTheSinkInTheOrderTheCallRaisedThem()
    {
        InMemoryAuditSink sink = new();
        AuditCallObserver observer = new(sink);
        var token = TestContext.Current.CancellationToken;

        await observer.OnCallEventAsync(Event(CallEventKind.CallStarted, ordinal: 0), token);

        // The two diagnostic facts between them take no number, so the chain stays gap-free.
        await observer.OnCallEventAsync(Event(CallEventKind.ModerationClean), token);
        await observer.OnCallEventAsync(Event(CallEventKind.TurnCompleted, ordinal: 1, turnIndex: 0), token);
        await observer.OnCallEventAsync(Event(CallEventKind.EmptyReply, turnIndex: 0), token);
        await observer.OnCallEventAsync(Event(CallEventKind.CallEnded, ordinal: 2), token);

        Assert.Equal([0, 1, 2], sink.EventsOf(CallId).Select(item => item.Sequence));
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
        long? ordinal = null,
        int? turnIndex = null,
        long? amends = null,
        IReadOnlyDictionary<string, string>? payload = null) => new()
        {
            CallId = CallId,
            Kind = kind,
            OccurredAt = Moment,
            Ordinal = ordinal,
            TurnIndex = turnIndex,
            AmendsOrdinal = amends,
            Payload = payload ?? new Dictionary<string, string>(StringComparer.Ordinal),
        };
}
