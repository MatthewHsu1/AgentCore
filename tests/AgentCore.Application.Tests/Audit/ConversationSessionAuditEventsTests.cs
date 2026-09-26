using AgentCore.TestSupport;
using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Runtime;
using AgentCore.Domain;
using AgentCore.Domain.Audit;
using Microsoft.Extensions.AI;
using Xunit;
using static AgentCore.Application.Tests.Audit.ConversationSessionAuditTestSupport;

namespace AgentCore.Application.Tests.Audit
{
    /// <summary>
    /// The turn loop writes the audit chain of D23, and the sink never sits on the turn.
    /// </summary>
    public sealed class ConversationSessionAuditEventsTests
    {
        // The events, and the identity a later amendment names.
        [Fact]
        public void ANewSession_WritesTheConversationStartedEvent()
        {
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(StayingNull);
            InMemoryAuditSink sink = new();

            ConversationSession session = Build(PolicyYaml, reply, fill, auditSink: sink).Create("conversation-1");

            AuditEvent started = Assert.Single(sink.EventsOf("conversation-1"));
            Assert.Equal(AuditEventKind.ConversationStarted, started.Kind);
            Assert.NotEqual(Guid.Empty, started.EventId);
            Assert.Null(started.TurnIndex);
            Assert.Null(started.AmendsEventId);
            Assert.Equal(session.ConversationId, started.ConversationId);
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

            IReadOnlyList<AuditEvent> written = sink.EventsOf("conversation-1");

            // The shape is the assertion, and both halves of it are the point.
            //
            // ARRIVAL: a second session that restarted its counter at zero would have every event it raised
            // refused by store 3, and a resumed conversation would lose its whole audit trail. The proof is
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

            AuditEvent completed = Assert.Single(sink.EventsOf("conversation-1"), item => item.Kind == AuditEventKind.TurnCompleted);
            Assert.Same(completed, sink.EventsOf("conversation-1")[1]);
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

            IReadOnlyList<AuditEvent> events = sink.EventsOf("conversation-1");
            AuditEvent completed = Assert.Single(events, item => item.Kind == AuditEventKind.TurnCompleted);
            AuditEvent cut = Assert.Single(events, item => item.Kind == AuditEventKind.ReplyInterrupted);

            // T23: the chain is append-only, so an amendment is a second event that references the first.
            IReadOnlyList<AuditEvent> chain = sink.EventsOf("conversation-1");
            Assert.Equal(completed.EventId, cut.AmendsEventId);
            Assert.True(chain.ToList().IndexOf(cut) > chain.ToList().IndexOf(completed));
            Assert.Equal(completed.TurnIndex, cut.TurnIndex);

            // Item 6a: the event records the text the caller ACTUALLY HEARD. Nothing here is estimated,
            // because the relay reported both values on its interrupt frame.
            Assert.Equal(AuditHash.OfText("hel").Value, cut.Payload[AuditPayloadKeys.UtteranceUntilInterruptSha256]);
            Assert.Equal("120", cut.Payload[AuditPayloadKeys.DurationUntilInterruptMs]);
        }
    }
}
