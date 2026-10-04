using AgentCore.TestSupport;
using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Tests.Diagnostics;
using AgentCore.Application.Tests.Runtime;
using AgentCore.Domain;
using AgentCore.Domain.Audit;
using Microsoft.Extensions.Logging;
using Xunit;
using AgentCore.Application.Runtime.Session;
using static AgentCore.Application.Tests.Audit.ConversationSessionAuditTestSupport;

namespace AgentCore.Application.Tests.Audit
{
    /// <summary>
    /// From the sink's side: the chain the sink holds verifies, the write never sits
    /// on the turn, and a sink that misbehaves — or is absent — never harms the caller.
    /// </summary>
    public sealed class ConversationSessionAuditSinkTests
    {
        // The chain the events form verifies.
        [Fact]
        public async Task TheEventsOfOneConversation_FormAChainThatVerifies()
        {
            using SequencedChatClient reply = new("hello there.", "goodbye.");
            using SequencedChatClient fill = new(StayingNull, SaidGoodbye);
            InMemoryAuditSink sink = new();
            ConversationSession session = Build(PolicyYaml, reply, fill, auditSink: sink).Create("conversation-1");

            CancellationToken token = TestContext.Current.CancellationToken;
            _ = await session.RunTurnAsync("hi", token);
            _ = await session.RunTurnAsync("goodbye", token);

            IReadOnlyList<AuditEvent> events = await session.RowsAsync(sink);

            // Four facts, four identities, none of them repeated. What orders them is
            // audit_event.sequence, which the store assigns and which this test never sees.
            Assert.Equal(4, events.Count);
            Assert.Equal(events.Count, events.Select(item => item.EventId).Distinct().Count());

            // This is the chain check, run over what the turn loop produced.
            Assert.All(events, AuditEventVocabulary.Validate);
        }

        // The write never sits on the turn.
        [Fact]
        public async Task TheTurn_FinishesWhileTheAppendIsStillOpen()
        {
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(StayingNull);
            BlockingAuditSink sink = new();
            ConversationSession session = Build(PolicyYaml, reply, fill, auditSink: sink).Create("conversation-1");

            // A durable insert costs 13 ms at p50 and 32 ms at p99, against 91 nanoseconds to
            // enqueue. The turn therefore finishes with no append complete at all.
            TurnResult turn = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            Assert.Equal("hello there.", turn.ReplyText);

            // The whole turn ran and returned while conversation.started is still inside the sink. The
            // turn.completed event is queued behind it rather than racing past it, because the audit hook takes
            // the notices of one conversation in the order the conversation raised them, so exactly one has arrived.
            await sink.Entered;
            _ = Assert.Single(sink.Events);

            sink.Release();

            // Off the turn, and only now. The queued event lands on the audit hook's own reader, which is
            // where a slow sink is paid for.
            await session.FlushNoticesAsync();

            Assert.Equal(
                [AuditEventKind.ConversationStarted, AuditEventKind.TurnCompleted],
                sink.Events.Select(item => item.Kind).ToArray());
        }

        [Fact]
        public async Task ASinkThatRefusesAnEvent_IsLoggedAndTheTurnGoesOn()
        {
            RecordingLogger logger = new();
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(StayingNull);
            ConversationSession session = Build(PolicyYaml, reply, fill, auditSink: new ThrowingAuditSink(), logger: logger)
                .Create("conversation-1");

            TurnResult turn = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);
            await session.FlushNoticesAsync();

            // Audit is a record of the conversation and never a part of it. Each refused row is a gap, logged once.
            Assert.Equal("hello there.", turn.ReplyText);
            Assert.Equal(2, logger.Of(5).Count);
            Assert.All(logger.Of(5), line => Assert.Equal(LogLevel.Error, line.Level));
        }

        [Fact]
        public async Task ASessionWithNoSink_RunsATurnAndThrowsNothing()
        {
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(StayingNull);
            ConversationSession session = Build(PolicyYaml, reply, fill).Create("conversation-1");

            TurnResult turn = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            Assert.Equal("hello there.", turn.ReplyText);
        }

        // The in-memory sink.
        [Fact]
        public async Task TheInMemorySink_KeepsTheConversationsApart()
        {
            using SequencedChatClient reply = new("hello there.", "hello again.");
            using SequencedChatClient fill = new(StayingNull, StayingNull);
            InMemoryAuditSink sink = new();
            ConversationSessionFactory factory = Build(PolicyYaml, reply, fill, auditSink: sink);

            ConversationSession first = factory.Create("conversation-1");
            ConversationSession second = factory.Create("conversation-2");
            _ = await first.RunTurnAsync("hi", TestContext.Current.CancellationToken);
            _ = await second.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            // One sink serves every conversation, because a session names itself on every event.
            Assert.All(await first.RowsAsync(sink), row => Assert.Equal("conversation-1", row.ConversationId));
            Assert.All(await second.RowsAsync(sink), row => Assert.Equal("conversation-2", row.ConversationId));
            Assert.Equal(2, sink.EventsOf("conversation-1").Count);
            Assert.Equal(2, sink.EventsOf("conversation-2").Count);
            Assert.Equal(4, sink.Events.Count);
        }
    }
}
