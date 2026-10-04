using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.BuiltIn;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Tests.Diagnostics;
using AgentCore.Domain.Audit;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentCore.Application.Tests.Hooks
{
    public sealed class AuditHookTests
    {
        private const string HelloSha256 = "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824";

        private static readonly DateTimeOffset Moment = new(2026, 8, 15, 9, 30, 0, TimeSpan.Zero);

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static HookScope Scope(int? turn = 0) => new("conversation-1", "main", turn, "", Guid.CreateVersion7(), 3, Moment);

        private static async Task<AuditEvent> RowOfAsync(Func<AgentHook, ValueTask> deliver)
        {
            InMemoryAuditSink sink = new();
            await deliver(BuiltInHooks.Create(sink).OfType<AuditHook>().Single());
            return Assert.Single(sink.EventsOf("conversation-1"));
        }

        [Fact]
        public async Task ATurnCompletedIsATurnCompletedRowWithTheReplyHash()
        {
            TurnCompleted completed = new(Scope(), TurnOutcome.Answered, "hi", "hello", "a", "b", TimeSpan.FromSeconds(1), null, false, null) { EventId = Guid.CreateVersion7() };

            AuditEvent row = await RowOfAsync(hook => hook.OnTurnCompletedAsync(completed, Ct));

            Assert.Equal((AuditEventKind.TurnCompleted, completed.EventId, Moment, (int?)0), (row.Kind, row.EventId, row.OccurredAt, row.TurnIndex));
            Assert.Equal(HelloSha256, row.Payload[AuditPayloadKeys.ReplyTextSha256]);
            Assert.Equal(("a", "b"), (row.Payload[AuditPayloadKeys.StageBefore], row.Payload[AuditPayloadKeys.StageAfter]));
            AuditEventVocabulary.Validate(row);
        }

        [Fact]
        public async Task AReplyCutAmendsItsTurnAndHashesWhatWasHeard()
        {
            Guid completed = Guid.CreateVersion7();
            ReplyCut cut = new(Scope(), "hello", TimeSpan.FromMilliseconds(1820), completed) { EventId = Guid.CreateVersion7() };

            AuditEvent row = await RowOfAsync(hook => hook.OnReplyCutAsync(cut, Ct));

            Assert.Equal((AuditEventKind.ReplyInterrupted, (Guid?)completed), (row.Kind, row.AmendsEventId));
            Assert.Equal(HelloSha256, row.Payload[AuditPayloadKeys.UtteranceUntilInterruptSha256]);
            Assert.Equal("1820", row.Payload[AuditPayloadKeys.DurationUntilInterruptMs]);
            AuditEventVocabulary.Validate(row);
        }

        [Fact]
        public async Task AFlaggedInputIsAPromptFlaggedRowWithItsCategoriesInOrder()
        {
            InputModerated flagged = new(Scope(), InputVerdict.Flagged, ["violence", "harassment"], null) { EventId = Guid.CreateVersion7() };

            AuditEvent row = await RowOfAsync(hook => hook.OnInputModeratedAsync(flagged, Ct));

            Assert.Equal(AuditEventKind.PromptFlagged, row.Kind);
            Assert.Equal("violence,harassment", row.Payload[AuditPayloadKeys.ModerationCategories]);
        }

        // Failed (fatal) and Undeclared are audited; TimedOut, Blocked and the rest are not.
        [Theory]
        [InlineData(ToolOutcome.Failed, true, true)]
        [InlineData(ToolOutcome.Undeclared, false, true)]
        [InlineData(ToolOutcome.Failed, false, false)]
        [InlineData(ToolOutcome.TimedOut, false, false)]
        [InlineData(ToolOutcome.Blocked, false, false)]
        [InlineData(ToolOutcome.Ok, false, false)]
        public async Task OnlyAFatalFailureOrAnUndeclaredToolIsAToolFailedRow(ToolOutcome outcome, bool fatal, bool audited)
        {
            InMemoryAuditSink sink = new();
            ToolFailureKind? kind = outcome == ToolOutcome.Undeclared ? ToolFailureKind.Undeclared : ToolFailureKind.Faulted;
            ToolCalled called = new(Scope(), "lookup_order", "call-1", TimeSpan.Zero, outcome, fatal, kind, "boom") { EventId = Guid.CreateVersion7() };

            await BuiltInHooks.Create(sink).OfType<AuditHook>().Single().OnToolCalledAsync(called, Ct);

            Assert.Equal(audited ? 1 : 0, sink.EventsOf("conversation-1").Count);
            if (audited)
            {
                AuditEvent row = sink.EventsOf("conversation-1")[0];
                Assert.Equal(("lookup_order", "call-1", "boom"), (row.Payload[AuditPayloadKeys.ToolName], row.Payload[AuditPayloadKeys.ToolCallId], row.Payload[AuditPayloadKeys.ToolError]));
                Assert.Equal(ToolFailureKinds.ToToken(kind!.Value), row.Payload[AuditPayloadKeys.ToolFailureKind]);
            }
        }

        // A refusal after the end is no row.
        [Fact]
        public async Task ARefusalIsARowUnlessTheConversationHadEnded()
        {
            InMemoryAuditSink sink = new();
            AgentHook hook = BuiltInHooks.Create(sink).OfType<AuditHook>().Single();

            await hook.OnTurnRefusedAsync(new TurnRefused(Scope(turn: null), TurnRefusal.InUse, AfterEnd: false) { EventId = Guid.CreateVersion7() }, Ct);
            await hook.OnTurnRefusedAsync(new TurnRefused(Scope(turn: null), TurnRefusal.Terminal, AfterEnd: true) { EventId = Guid.CreateVersion7() }, Ct);

            AuditEvent row = Assert.Single(sink.EventsOf("conversation-1"));
            Assert.Equal((AuditEventKind.TurnRefused, "in_use"), (row.Kind, row.Payload[AuditPayloadKeys.RefusedReason]));
        }

        [Fact]
        public async Task TheEndCarriesItsReasonTokenAndTerminalStage()
        {
            ConversationEnded ended = new(Scope(turn: null), ConversationEndReason.AgentCompleted, "done", null) { EventId = Guid.CreateVersion7() };

            AuditEvent row = await RowOfAsync(hook => hook.OnConversationEndedAsync(ended, Ct));

            Assert.Equal(AuditEventKind.ConversationEnded, row.Kind);
            Assert.Equal(ConversationEndReasons.ToToken(ConversationEndReason.AgentCompleted), row.Payload[AuditPayloadKeys.EndReason]);
            Assert.Equal("done", row.Payload[AuditPayloadKeys.StageAfter]);
        }

        [Fact]
        public async Task ASupersedeAndAStartAreRowsToo()
        {
            InMemoryAuditSink sink = new();
            AgentHook hook = BuiltInHooks.Create(sink).OfType<AuditHook>().Single();

            await hook.OnConversationStartedAsync(new ConversationStarted(Scope(turn: null), ConversationOrigin.New) { EventId = Guid.CreateVersion7() }, Ct);
            await hook.OnTurnSupersededAsync(new TurnSuperseded(Scope(turn: 3), 1, 2) { EventId = Guid.CreateVersion7() }, Ct);

            Assert.Equal([AuditEventKind.ConversationStarted, AuditEventKind.TurnSuperseded], sink.EventsOf("conversation-1").Select(row => row.Kind));
            Assert.Equal(("1", "2"), (sink.EventsOf("conversation-1")[1].Payload[AuditPayloadKeys.WithdrewFromTurnIndex], sink.EventsOf("conversation-1")[1].Payload[AuditPayloadKeys.WithdrewThroughTurnIndex]));
        }

        // A refused row is a gap in the record: logged as AuditAppendFailed (EventId 5, Error), then thrown on to the engine.
        [Fact]
        public async Task ARowTheSinkRefusesIsLoggedAndThrownOn()
        {
            RecordingLogger logger = new();
            AgentHook hook = BuiltInHooks.Create(new ThrowingAuditSink(), logger).OfType<AuditHook>().Single();
            TurnCompleted completed = new(Scope(), TurnOutcome.Answered, "hi", "hello", "a", "b", TimeSpan.FromSeconds(1), null, false, null) { EventId = Guid.CreateVersion7() };

            _ = await Assert.ThrowsAsync<InvalidOperationException>(() => hook.OnTurnCompletedAsync(completed, Ct).AsTask());

            LogLine line = Assert.Single(logger.Of(5));
            Assert.Equal(LogLevel.Error, line.Level);
        }

        // The audit hook waits out a slow store rather than dropping.
        [Fact]
        public void TheAuditHookNeverTimesOut()
        {
            Assert.Null(BuiltInHooks.Create(new InMemoryAuditSink()).OfType<AuditHook>().Single().NoticeTimeout);
        }
    }
}
