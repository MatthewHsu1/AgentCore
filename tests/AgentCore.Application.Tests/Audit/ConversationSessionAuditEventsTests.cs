using AgentCore.TestSupport;
using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Runtime;
using AgentCore.Domain;
using AgentCore.Domain.Audit;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Cut;
using AgentCore.Application.Runtime.Session;
using static AgentCore.Application.Tests.Audit.ConversationSessionAuditTestSupport;

namespace AgentCore.Application.Tests.Audit
{
    /// <summary>
    /// The turn loop writes the audit chain, and the sink never sits on the turn.
    /// </summary>
    public sealed class ConversationSessionAuditEventsTests
    {
        // The events, and the identity a later amendment names.
        // The start is raised when the session opens the store, so a turn runs first.
        [Fact]
        public async Task AFirstTurn_WritesTheConversationStartedEventFirst()
        {
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(StayingNull);
            InMemoryAuditSink sink = new();

            ConversationSession session = Build(PolicyYaml, reply, fill, auditSink: sink).Create("conversation-1");
            _ = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            AuditEvent started = (await session.RowsAsync(sink))[0];
            Assert.Equal(AuditEventKind.ConversationStarted, started.Kind);
            Assert.NotEqual(Guid.Empty, started.EventId);
            Assert.Null(started.TurnIndex);
            Assert.Null(started.AmendsEventId);
            Assert.Equal(session.ConversationId, started.ConversationId);
        }

        // The audit hook alone writes the rows, so one turn is exactly two of them.
        [Fact]
        public async Task OneTurnWritesEachOfItsRowsOnce()
        {
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(StayingNull);
            InMemoryAuditSink sink = new();
            ConversationSession session = Build(PolicyYaml, reply, fill, auditSink: sink).Create("conversation-1");

            _ = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            Assert.Equal([AuditEventKind.ConversationStarted, AuditEventKind.TurnCompleted], (await session.RowsAsync(sink)).Select(row => row.Kind));
        }

        [Fact]
        public async Task ASecondSessionOfOneConversation_CollidesWithNothing()
        {
            using SequencedChatClient reply = new("hello there.", "still here.");
            using SequencedChatClient fill = new(StayingNull, StayingNull);
            InMemoryAuditSink sink = new();

            // One factory, so both sessions share one compiled agent and one store — which is what a host
            // holds, and what makes the second session a resume rather than a different conversation.
            ConversationSessionFactory factory = Build(PolicyYaml, reply, fill, auditSink: sink);

            ConversationSession first = factory.Create("conversation-1");
            _ = await first.RunTurnAsync("hello", TestContext.Current.CancellationToken);
            await first.FlushTranscriptAsync();

            ConversationSession second = factory.Create("conversation-1");
            _ = await second.RunTurnAsync("still there?", TestContext.Current.CancellationToken);
            await second.FlushTranscriptAsync();

            IReadOnlyList<AuditEvent> written = await second.RowsAsync(sink);

            // The shape is the assertion, and both halves of it are the point.
            //
            // ARRIVAL: a second session that restarted its counter at zero would have every event it raised
            // refused by the store, and a resumed conversation would lose its whole audit trail. The proof is
            // that the second session's two events are HERE — distinctness proves nothing,
            // because InMemoryAuditSink enforces no key and Guid.CreateVersion7 is unique by
            // construction, so that assertion passed just as well over the two events that never arrived.
            //
            // A SECOND conversation.started: a session opening onto a conversation that already has words raises one, and
            // it is kept rather than suppressed. See AuditEventKind.ConversationStarted for why. This is where
            // that decision is visible, so a build that quietly stopped raising it fails here.
            Assert.Equal(
                [
                    AuditEventKind.ConversationStarted,
                    AuditEventKind.TurnCompleted,
                    AuditEventKind.ConversationStarted,
                    AuditEventKind.TurnCompleted,
                ],
                written.Select(item => item.Kind).ToArray());

            Guid[] ids = [.. written.Select(item => item.EventId)];
            Assert.Equal(ids.Length, ids.Distinct().Count());
            Assert.DoesNotContain(Guid.Empty, ids);
        }

        [Fact]
        public async Task AFinishedTurn_WritesOneTurnCompletedEventWithBothStages()
        {
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(StayingNull);
            InMemoryAuditSink sink = new();
            TestTimeProvider clock = new();
            ConversationSession session = Build(PolicyYaml, reply, fill, timeProvider: clock, auditSink: sink).Create("conversation-1");

            clock.Advance(TimeSpan.FromSeconds(12));
            TurnResult turn = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            IReadOnlyList<AuditEvent> rows = await session.RowsAsync(sink);
            AuditEvent completed = Assert.Single(rows, item => item.Kind == AuditEventKind.TurnCompleted);
            Assert.Same(completed, rows[1]);
            Assert.Equal(0, completed.TurnIndex);
            Assert.Equal(AuditHash.OfText("hello there.").Value, completed.Payload[AuditPayloadKeys.ReplyTextSha256]);
            Assert.Equal("greeting", completed.Payload[AuditPayloadKeys.StageBefore]);
            Assert.Equal("greeting", completed.Payload[AuditPayloadKeys.StageAfter]);

            // The moment comes from the injected clock and not from the sink. A background writer would
            // stamp it one enqueue late.
            Assert.Equal(clock.GetUtcNow(), completed.OccurredAt);
            Assert.Equal(completed.OccurredAt, turn.EndedAt);
        }

        [Fact]
        public async Task ABargeIn_WritesASecondEventThatAmendsTheTurnEvent()
        {
            using ScriptedChatClient reply = new("hello", " there.") { GateAfterFirstFragment = true };
            using SequencedChatClient fill = new(StayingNull);
            InMemoryAuditSink sink = new();
            ConversationSession session = Build(PolicyYaml, reply, fill, auditSink: sink).Create("conversation-1");

            // The caller heard the first fragment, then spoke over the rest.
            await using (IAsyncEnumerator<ChatResponseUpdate> updates = session
                .RunTurnStreamingAsync("hi", TestContext.Current.CancellationToken)
                .GetAsyncEnumerator(TestContext.Current.CancellationToken))
            {
                Assert.True(await updates.MoveNextAsync());
                Assert.True(session.Cut(0, new TurnCut("hel", TimeSpan.FromMilliseconds(120))));
                Assert.False(await updates.MoveNextAsync());
            }

            IReadOnlyList<AuditEvent> events = await session.RowsAsync(sink);
            AuditEvent completed = Assert.Single(events, item => item.Kind == AuditEventKind.TurnCompleted);
            AuditEvent cut = Assert.Single(events, item => item.Kind == AuditEventKind.ReplyInterrupted);

            // The chain is append-only, so an amendment is a second event that references the first.
            IReadOnlyList<AuditEvent> chain = events;
            Assert.Equal(completed.EventId, cut.AmendsEventId);
            Assert.Equal(completed.OccurredAt, cut.OccurredAt);
            Assert.True(chain.ToList().IndexOf(cut) > chain.ToList().IndexOf(completed));
            Assert.Equal(completed.TurnIndex, cut.TurnIndex);

            // The event records the text the caller ACTUALLY HEARD. Nothing here is estimated,
            // because the relay reported both values on its interrupt frame.
            Assert.Equal(AuditHash.OfText("hel").Value, cut.Payload[AuditPayloadKeys.UtteranceUntilInterruptSha256]);
            Assert.Equal("120", cut.Payload[AuditPayloadKeys.DurationUntilInterruptMs]);
        }
    }
}
