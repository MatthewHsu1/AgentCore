using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Audit;
using AgentCore.Application.Runtime;
using AgentCore.Domain.Audit;
using Xunit;
using Xunit.Sdk;

namespace AgentCore.Application.Tests.Audit
{
    /// <summary>
    /// The one place that knows both vocabularies: a neutral fact of a conversation, and a row of the chain of D23.
    /// </summary>
    public sealed class AuditConversationObserverTests
    {
        private const string ConversationId = "conversation-1";

        private static readonly DateTimeOffset Moment = new(2026, 8, 15, 9, 30, 0, TimeSpan.Zero);

        /// <summary>The kinds that are counted or logged or both, and stored nowhere.</summary>
        public static TheoryData<ConversationEventKind> DiagnosticKinds =>
        [
            ConversationEventKind.ModerationUnavailable,
            ConversationEventKind.ModerationClean,
            ConversationEventKind.EmptyReply,
            ConversationEventKind.ExtractionFailed,
            ConversationEventKind.TranscriptWriteFailed,
            ConversationEventKind.StateRestorePartial,
            ConversationEventKind.TranscriptResyncFailed,
            ConversationEventKind.RunFaulted,
        ];

        /// <summary>Every kind, once, across the two tables above.</summary>
        [Fact]
        public void TheTwoTables_BetweenThemNameEveryKind()
        {
            IEnumerable<ConversationEventKind> named =
            [
                .. DiagnosticKinds.Select(row => (ConversationEventKind)((ITheoryDataRow)row).GetData()[0]!),
                .. StoredKinds.Select(row => (ConversationEventKind)((ITheoryDataRow)row).GetData()[0]!),
            ];

            Assert.Equal([.. Enum.GetValues<ConversationEventKind>().Order()], [.. named.Order()]);
        }

        /// <summary>The eight kinds the store keeps, beside the token each one writes.</summary>
        public static TheoryData<ConversationEventKind, AuditEventKind> StoredKinds =>
            new()
            {
                { ConversationEventKind.ConversationStarted, AuditEventKind.ConversationStarted },
                { ConversationEventKind.PromptFlagged, AuditEventKind.PromptFlagged },
                { ConversationEventKind.ToolFailed, AuditEventKind.ToolFailed },
                { ConversationEventKind.TurnCompleted, AuditEventKind.TurnCompleted },
                { ConversationEventKind.ReplyInterrupted, AuditEventKind.ReplyInterrupted },
                { ConversationEventKind.ConversationEnded, AuditEventKind.ConversationEnded },
                { ConversationEventKind.TurnSuperseded, AuditEventKind.TurnSuperseded },
                { ConversationEventKind.TurnRefused, AuditEventKind.TurnRefused },
            };

        [Theory]
        [MemberData(nameof(DiagnosticKinds))]
        public async Task AFactWithNoEventId_WritesNoRow(ConversationEventKind kind)
        {
            InMemoryAuditSink sink = new();
            AuditConversationObserver observer = new(sink);

            // A diagnostic-only event leaves EventId null precisely because it takes no row.
            await observer.OnConversationEventAsync(Event(kind), TestContext.Current.CancellationToken);

            Assert.Empty(sink.Events);
        }

        [Theory]
        [MemberData(nameof(StoredKinds))]
        public async Task AStoredFact_BecomesARowOfItsOwnKind(ConversationEventKind kind, AuditEventKind expected)
        {
            InMemoryAuditSink sink = new();
            AuditConversationObserver observer = new(sink);

            await observer.OnConversationEventAsync(
                Event(kind, eventId: Guid.CreateVersion7(), amends: Guid.CreateVersion7()),
                TestContext.Current.CancellationToken);

            AuditEvent written = Assert.Single(sink.Events);
            Assert.Equal(expected, written.Kind);
        }

        [Fact]
        public async Task ItCopiesTheIdentityStraightThrough()
        {
            InMemoryAuditSink sink = new();
            AuditConversationObserver observer = new(sink);
            Guid id = Guid.CreateVersion7();

            await observer.OnConversationEventAsync(
                new ConversationEvent
                {
                    ConversationId = ConversationId,
                    Kind = ConversationEventKind.ConversationStarted,
                    OccurredAt = DateTimeOffset.UnixEpoch,
                    EventId = id,
                },
                CancellationToken.None);

            Assert.Equal(id, Assert.Single(sink.EventsOf(ConversationId)).EventId);
        }

        [Fact]
        public async Task ItCopiesTheAmendmentStraightThrough()
        {
            InMemoryAuditSink sink = new();
            AuditConversationObserver observer = new(sink);
            Guid turn = Guid.CreateVersion7();
            Guid cut = Guid.CreateVersion7();

            await observer.OnConversationEventAsync(
                new ConversationEvent
                {
                    ConversationId = ConversationId,
                    Kind = ConversationEventKind.ReplyInterrupted,
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

            AuditEvent written = Assert.Single(sink.EventsOf(ConversationId));
            Assert.Equal(cut, written.EventId);
            Assert.Equal(turn, written.AmendsEventId);
        }

        [Fact]
        public async Task AFactThatCorrectsNothing_LeavesTheAmendmentUnset()
        {
            InMemoryAuditSink sink = new();
            AuditConversationObserver observer = new(sink);

            await observer.OnConversationEventAsync(
                Event(ConversationEventKind.TurnCompleted, eventId: Guid.CreateVersion7()),
                TestContext.Current.CancellationToken);

            Assert.Null(Assert.Single(sink.Events).AmendsEventId);
        }

        [Fact]
        public async Task TheRow_CarriesTheConversationTheTurnAndTheMomentUnchanged()
        {
            InMemoryAuditSink sink = new();
            AuditConversationObserver observer = new(sink);
            Dictionary<string, string> payload = new(StringComparer.Ordinal)
            {
                [AuditPayloadKeys.ReplyTextSha256] = AuditHash.OfText("hello there.").Value,
                [AuditPayloadKeys.StageBefore] = "greeting",
                [AuditPayloadKeys.StageAfter] = "triage",
            };

            await observer.OnConversationEventAsync(
                Event(ConversationEventKind.TurnCompleted, eventId: Guid.CreateVersion7(), turnIndex: 5, payload: payload),
                TestContext.Current.CancellationToken);

            AuditEvent written = Assert.Single(sink.Events);
            Assert.Equal(ConversationId, written.ConversationId);
            Assert.Equal(5, written.TurnIndex);
            Assert.Equal(Moment, written.OccurredAt);
            Assert.Equal(payload, written.Payload);
        }

        [Fact]
        public async Task AFactAboutTheConversationItself_CarriesNoTurn()
        {
            InMemoryAuditSink sink = new();
            AuditConversationObserver observer = new(sink);

            await observer.OnConversationEventAsync(
                Event(ConversationEventKind.ConversationStarted, eventId: Guid.CreateVersion7()),
                TestContext.Current.CancellationToken);

            Assert.Null(Assert.Single(sink.Events).TurnIndex);
        }

        [Fact]
        public async Task ManyFacts_ReachTheSinkInTheOrderTheConversationRaisedThem()
        {
            InMemoryAuditSink sink = new();
            AuditConversationObserver observer = new(sink);
            CancellationToken token = TestContext.Current.CancellationToken;

            await observer.OnConversationEventAsync(Event(ConversationEventKind.ConversationStarted, eventId: Guid.CreateVersion7()), token);

            // The two diagnostic facts between them take no identity, so they write no row.
            await observer.OnConversationEventAsync(Event(ConversationEventKind.ModerationClean), token);
            await observer.OnConversationEventAsync(
                Event(ConversationEventKind.TurnCompleted, eventId: Guid.CreateVersion7(), turnIndex: 0), token);
            await observer.OnConversationEventAsync(Event(ConversationEventKind.EmptyReply, turnIndex: 0), token);
            await observer.OnConversationEventAsync(Event(ConversationEventKind.ConversationEnded, eventId: Guid.CreateVersion7()), token);

            Assert.Equal(
                [AuditEventKind.ConversationStarted, AuditEventKind.TurnCompleted, AuditEventKind.ConversationEnded],
                sink.EventsOf(ConversationId).Select(item => item.Kind).ToArray());
        }

        [Fact]
        public void NoSink_IsRefused()
        {
            _ = Assert.Throws<ArgumentNullException>(() => new AuditConversationObserver(null!));
        }

        [Fact]
        public async Task NoEvent_IsRefused()
        {
            _ = await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                        await new AuditConversationObserver(new InMemoryAuditSink())
                            .OnConversationEventAsync(null!, TestContext.Current.CancellationToken));
        }

        private static ConversationEvent Event(
            ConversationEventKind kind,
            Guid? eventId = null,
            int? turnIndex = null,
            Guid? amends = null,
            IReadOnlyDictionary<string, string>? payload = null)
        {
            return new()
            {
                ConversationId = ConversationId,
                Kind = kind,
                OccurredAt = Moment,
                EventId = eventId,
                TurnIndex = turnIndex,
                AmendsEventId = amends,
                Payload = payload ?? RequiredPayload(kind),
            };
        }

        /// <summary>
        /// The payload each kind must carry to be a legal event, per <see cref="AuditEventVocabulary"/>.
        /// </summary>
        private static Dictionary<string, string> RequiredPayload(ConversationEventKind kind)
        {
            return kind switch
            {
                ConversationEventKind.ReplyInterrupted => new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AuditPayloadKeys.UtteranceUntilInterruptSha256] = AuditHash.OfText("the belt ships").Value,
                },
                ConversationEventKind.PromptFlagged => new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AuditPayloadKeys.ModerationCategories] = "harassment",
                },
                ConversationEventKind.ConversationEnded => new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AuditPayloadKeys.EndReason] = ConversationEndReasons.ToToken(ConversationEndReason.CallerHungUp),
                },
                ConversationEventKind.ConversationStarted
                    or ConversationEventKind.ModerationUnavailable
                    or ConversationEventKind.ModerationClean
                    or ConversationEventKind.ToolFailed
                    or ConversationEventKind.EmptyReply
                    or ConversationEventKind.ExtractionFailed
                    or ConversationEventKind.TurnCompleted
                    or ConversationEventKind.TurnSuperseded
                    or ConversationEventKind.TurnRefused
                    or ConversationEventKind.TranscriptWriteFailed
                    or ConversationEventKind.StateRestorePartial
                    or ConversationEventKind.TranscriptResyncFailed
                    or ConversationEventKind.RunFaulted => new Dictionary<string, string>(StringComparer.Ordinal),
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "The conversation event vocabulary is closed, and this value is not in it."),
            };
        }
    }
}
