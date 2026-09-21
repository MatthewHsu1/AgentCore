using AgentCore.Application.Diagnostics;
using AgentCore.Application.Runtime;
using AgentCore.Domain.Audit;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentCore.Application.Tests.Diagnostics
{
    /// <summary>
    /// The "log once" rows of section 8.7, written from a fact instead of from the turn loop.
    /// </summary>
    /// <remarks>
    /// Moving the conversation sites behind the hook must change neither the text an operator greps for nor how
    /// often it appears, so these tests pin the event id, the level, and the fields of each of the six
    /// lines. They also pin the six kinds that write nothing here: a normal conversation is recorded by the chain
    /// of D23, and a log is not.
    /// </remarks>
    public sealed class LoggingConversationObserverTests
    {
        private const string ConversationId = "conversation-1";

        /// <summary>The ids the source-generated methods carry. They are unchanged by the hook.</summary>
        private const int ExtractionFailedEventId = 1;
        private const int ToolBudgetSpentEventId = 2;
        private const int EmptyReplyEventId = 3;
        private const int PromptRefusedEventId = 6;
        private const int ModerationUnavailableEventId = 7;
        private const int StateRestorePartialEventId = 13;

        /// <summary>The kinds this observer writes no line for.</summary>
        public static TheoryData<ConversationEventKind> QuietKinds =>
        [
            ConversationEventKind.ConversationStarted,
            ConversationEventKind.TurnCompleted,
            ConversationEventKind.ReplyInterrupted,
            ConversationEventKind.ConversationEnded,

            // Logged, but not here: the line is written where the write was refused, which is the only
            // place the exception still exists. A line from this observer too would double it.
            ConversationEventKind.TranscriptWriteFailed,

            // The quietest of them all: counted, and not logged anywhere.
            ConversationEventKind.ModerationClean,
        ];

        [Fact]
        public async Task ASpentToolBudget_IsAnErrorNamingTheTurnAndTheFault()
        {
            RecordingLogger logger = new();

            await Observe(
                logger,
                Event(ConversationEventKind.ToolFailed, turnIndex: 2, AuditPayloadKeys.ToolError, "the CRM refused."));

            LogLine line = Assert.Single(logger.Of(ToolBudgetSpentEventId));
            Assert.Equal(LogLevel.Error, line.Level);
            Assert.Equal(
                "A tool of conversation conversation-1 failed four times in turn 2: the CRM refused. "
                    + "The turn spoke the fallback and the conversation continues.",
                line.Message);
        }

        [Fact]
        public async Task AQuietRun_IsAWarningNamingTheTurn()
        {
            RecordingLogger logger = new();

            await Observe(logger, Event(ConversationEventKind.EmptyReply, turnIndex: 3));

            LogLine line = Assert.Single(logger.Of(EmptyReplyEventId));
            Assert.Equal(LogLevel.Warning, line.Level);
            Assert.Equal(
                "Turn 3 of conversation conversation-1 returned an empty reply, so it spoke the fallback. "
                    + "The run reached 40 tool rounds, or the model answered nothing.",
                line.Message);
        }

        [Fact]
        public async Task AnExtractorThatProducedNothing_IsAWarningAndNeverAnError()
        {
            RecordingLogger logger = new();

            // Row two: the slots stay unchanged and the conversation continues.
            await Observe(
                logger,
                Event(ConversationEventKind.ExtractionFailed, turnIndex: 0, ConversationEventPayloadKeys.Reason, "it timed out."));

            LogLine line = Assert.Single(logger.Of(ExtractionFailedEventId));
            Assert.Equal(LogLevel.Warning, line.Level);
            Assert.Equal(
                "The extractor of conversation conversation-1 produced nothing for turn 0: it timed out. "
                    + "The slots stay unchanged and the conversation continues.",
                line.Message);
        }

        [Fact]
        public async Task ARefusedPrompt_ReportsTheCategoriesAndNeverTheWords()
        {
            RecordingLogger logger = new();

            await Observe(
                logger,
                Event(
                    ConversationEventKind.PromptFlagged,
                    turnIndex: 1,
                    AuditPayloadKeys.ModerationCategories,
                    "harassment,violence"));

            LogLine line = Assert.Single(logger.Of(PromptRefusedEventId));
            Assert.Equal(LogLevel.Warning, line.Level);
            Assert.Equal(
                "Moderation flagged turn 1 of conversation conversation-1 for harassment,violence, "
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
                    ConversationEventKind.ModerationUnavailable,
                    turnIndex: 4,
                    ConversationEventPayloadKeys.Reason,
                    "it did not answer within 500 ms."));

            LogLine line = Assert.Single(logger.Of(ModerationUnavailableEventId));
            Assert.Equal(LogLevel.Warning, line.Level);
            Assert.Equal(
                "Moderation did not answer for turn 4 of conversation conversation-1 (it did not answer within 500 ms.). "
                    + "The turn ran unchecked, because moderation fails open.",
                line.Message);
        }

        [Fact]
        public async Task APartialStateRestore_IsAWarningNamingTheConversationAndWhatItLost()
        {
            RecordingLogger logger = new();

            // No turn index: the conversation is still opening. It is documented as logged, and until this line
            // existed it was not — a document change that cost every resumed conversation its stage produced no
            // line and no metric, and six tests of the restore itself passed straight over the silence.
            await Observe(
                logger,
                Event(
                    ConversationEventKind.StateRestorePartial,
                    turnIndex: null,
                    ConversationEventPayloadKeys.Reason,
                    "the document no longer declares the slot 'model'."));

            LogLine line = Assert.Single(logger.Of(StateRestorePartialEventId));
            Assert.Equal(LogLevel.Warning, line.Level);
            Assert.Equal(
                "Conversation conversation-1 could not restore part of its stored state: the document no longer declares "
                    + "the slot 'model'. The conversation resumes without that part.",
                line.Message);
        }

        [Theory]
        [MemberData(nameof(QuietKinds))]
        public async Task AKindTheChainRecords_WritesNoLine(ConversationEventKind kind)
        {
            RecordingLogger logger = new();

            await Observe(logger, Event(kind, turnIndex: 0));

            Assert.Empty(logger.Lines);
        }

        [Fact]
        public async Task NoLogger_IsStillAnObserver()
        {
            // The library never throws for want of one.
            ValueTask pending = new LoggingConversationObserver().OnConversationEventAsync(
                Event(ConversationEventKind.EmptyReply, turnIndex: 0),
                TestContext.Current.CancellationToken);

            await pending;
            Assert.True(pending.IsCompletedSuccessfully);
        }

        [Fact]
        public async Task NoEvent_IsRefused()
        {
            _ = await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                        await new LoggingConversationObserver()
                            .OnConversationEventAsync(null!, TestContext.Current.CancellationToken));
        }

        private static async Task Observe(ILogger logger, ConversationEvent conversationEvent)
        {
            LoggingConversationObserver observer = new(logger);

            // The observer never waits, so this always completes on this thread. It is awaited anyway,
            // because the port says nothing about which of the two it does.
            await observer.OnConversationEventAsync(conversationEvent, TestContext.Current.CancellationToken);
        }

        private static ConversationEvent Event(ConversationEventKind kind, int? turnIndex, string? key = null, string? detail = null)
        {
            Dictionary<string, string> payload = new(StringComparer.Ordinal);
            if (key is not null && detail is not null)
            {
                payload[key] = detail;
            }

            return new ConversationEvent
            {
                ConversationId = ConversationId,
                Kind = kind,
                OccurredAt = DateTimeOffset.UnixEpoch,
                TurnIndex = turnIndex,
                Payload = payload,
            };
        }
    }
}
