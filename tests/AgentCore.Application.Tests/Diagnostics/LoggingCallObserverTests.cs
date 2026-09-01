using AgentCore.Application.Diagnostics;
using AgentCore.Application.Runtime;
using AgentCore.Domain.Audit;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentCore.Application.Tests.Diagnostics;

/// <summary>
/// The "log once" rows of section 8.7, written from a fact instead of from the turn loop.
/// </summary>
/// <remarks>
/// Moving the call sites behind the hook must change neither the text an operator greps for nor how
/// often it appears, so these tests pin the event id, the level, and the fields of each of the six
/// lines. They also pin the six kinds that write nothing here: a normal call is recorded by the chain
/// of D23, and a log is not.
/// </remarks>
public sealed class LoggingCallObserverTests
{
    private const string CallId = "call-1";

    /// <summary>The ids the source-generated methods carry. They are unchanged by the hook.</summary>
    private const int ExtractionFailedEventId = 1;
    private const int ToolBudgetSpentEventId = 2;
    private const int EmptyReplyEventId = 3;
    private const int PromptRefusedEventId = 6;
    private const int ModerationUnavailableEventId = 7;
    private const int StateRestorePartialEventId = 13;

    /// <summary>The kinds this observer writes no line for.</summary>
    public static TheoryData<CallEventKind> QuietKinds =>
    [
        CallEventKind.CallStarted,
        CallEventKind.TurnCompleted,
        CallEventKind.ReplyInterrupted,
        CallEventKind.CallEnded,

        // Logged, but not here: the line is written where the write was refused, which is the only
        // place the exception still exists. A line from this observer too would double it.
        CallEventKind.TranscriptWriteFailed,

        // The quietest of them all: counted, and not logged anywhere.
        CallEventKind.ModerationClean,
    ];

    [Fact]
    public async Task ASpentToolBudget_IsAnErrorNamingTheTurnAndTheFault()
    {
        RecordingLogger logger = new();

        await Observe(
            logger,
            Event(CallEventKind.ToolFailed, turnIndex: 2, AuditPayloadKeys.ToolError, "the CRM refused."));

        var line = Assert.Single(logger.Of(ToolBudgetSpentEventId));
        Assert.Equal(LogLevel.Error, line.Level);
        Assert.Equal(
            "A tool of call call-1 failed four times in turn 2: the CRM refused. "
                + "The turn spoke the fallback and the call continues.",
            line.Message);
    }

    [Fact]
    public async Task AQuietRun_IsAWarningNamingTheTurn()
    {
        RecordingLogger logger = new();

        await Observe(logger, Event(CallEventKind.EmptyReply, turnIndex: 3));

        var line = Assert.Single(logger.Of(EmptyReplyEventId));
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Equal(
            "Turn 3 of call call-1 returned an empty reply, so it spoke the fallback. "
                + "The run reached 40 tool rounds, or the model answered nothing.",
            line.Message);
    }

    [Fact]
    public async Task AnExtractorThatProducedNothing_IsAWarningAndNeverAnError()
    {
        RecordingLogger logger = new();

        // Row two: the slots stay unchanged and the call continues.
        await Observe(
            logger,
            Event(CallEventKind.ExtractionFailed, turnIndex: 0, CallEventPayloadKeys.Reason, "it timed out."));

        var line = Assert.Single(logger.Of(ExtractionFailedEventId));
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Equal(
            "The extractor of call call-1 produced nothing for turn 0: it timed out. "
                + "The slots stay unchanged and the call continues.",
            line.Message);
    }

    [Fact]
    public async Task ARefusedPrompt_ReportsTheCategoriesAndNeverTheWords()
    {
        RecordingLogger logger = new();

        await Observe(
            logger,
            Event(
                CallEventKind.PromptFlagged,
                turnIndex: 1,
                AuditPayloadKeys.ModerationCategories,
                "harassment,violence"));

        var line = Assert.Single(logger.Of(PromptRefusedEventId));
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Equal(
            "Moderation flagged turn 1 of call call-1 for harassment,violence, "
                + "so the agent refused it and spoke the refusal line.",
            line.Message);
    }

    [Fact]
    public async Task AModerationEndpointThatDidNotAnswer_IsAWarningBecauseTheVendorIsNotThisLibrary()
    {
        RecordingLogger logger = new();

        await Observe(
            logger,
            Event(
                CallEventKind.ModerationUnavailable,
                turnIndex: 4,
                CallEventPayloadKeys.Reason,
                "it did not answer within 500 ms."));

        var line = Assert.Single(logger.Of(ModerationUnavailableEventId));
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Equal(
            "Moderation did not answer for turn 4 of call call-1 (it did not answer within 500 ms.). "
                + "The turn ran unchecked, because moderation fails open.",
            line.Message);
    }

    [Fact]
    public async Task APartialStateRestore_IsAWarningNamingTheCallAndWhatItLost()
    {
        RecordingLogger logger = new();

        // No turn index: the call is still opening. It is documented as logged, and until this line
        // existed it was not — a document change that cost every resumed call its stage produced no
        // line and no metric, and six tests of the restore itself passed straight over the silence.
        await Observe(
            logger,
            Event(
                CallEventKind.StateRestorePartial,
                turnIndex: null,
                CallEventPayloadKeys.Reason,
                "the document no longer declares the slot 'model'."));

        var line = Assert.Single(logger.Of(StateRestorePartialEventId));
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Equal(
            "Call call-1 could not restore part of its stored state: the document no longer declares "
                + "the slot 'model'. The call resumes without that part.",
            line.Message);
    }

    [Theory]
    [MemberData(nameof(QuietKinds))]
    public async Task AKindTheChainRecords_WritesNoLine(CallEventKind kind)
    {
        RecordingLogger logger = new();

        await Observe(logger, Event(kind, turnIndex: 0));

        Assert.Empty(logger.Lines);
    }

    [Fact]
    public async Task NoLogger_IsStillAnObserver()
    {
        // The library never throws for want of one.
        LoggingCallObserver observer = new();

        await observer.OnCallEventAsync(
            Event(CallEventKind.EmptyReply, turnIndex: 0),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task NoEvent_IsRefused()
        => await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await new LoggingCallObserver()
                .OnCallEventAsync(null!, TestContext.Current.CancellationToken));

    private static async Task Observe(ILogger logger, CallEvent callEvent)
    {
        LoggingCallObserver observer = new(logger);

        // The observer never waits, so this always completes on this thread. It is awaited anyway,
        // because the port says nothing about which of the two it does.
        await observer.OnCallEventAsync(callEvent, TestContext.Current.CancellationToken);
    }

    private static CallEvent Event(CallEventKind kind, int? turnIndex, string? key = null, string? detail = null)
    {
        Dictionary<string, string> payload = new(StringComparer.Ordinal);
        if (key is not null && detail is not null)
        {
            payload[key] = detail;
        }

        return new CallEvent
        {
            CallId = CallId,
            Kind = kind,
            OccurredAt = DateTimeOffset.UnixEpoch,
            TurnIndex = turnIndex,
            Payload = payload,
        };
    }
}
