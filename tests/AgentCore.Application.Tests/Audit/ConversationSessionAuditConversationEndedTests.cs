using AgentCore.TestSupport;
using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using AgentCore.Domain;
using AgentCore.Application.Tests.Runtime;
using AgentCore.Domain.Audit;
using Xunit;
using AgentCore.Application.Runtime.Session;
using static AgentCore.Application.Tests.Audit.ConversationSessionAuditTestSupport;

namespace AgentCore.Application.Tests.Audit
{
    /// <summary>The four ways a conversation can end, and what each one writes to the chain.</summary>
    public sealed class ConversationSessionAuditConversationEndedTests
    {
        [Fact]
        public async Task ATerminalStage_ClosesTheChainWithOneConversationEndedEvent()
        {
            using SequencedChatClient reply = new("goodbye.");
            using SequencedChatClient fill = new(SaidGoodbye);
            InMemoryAuditSink sink = new();
            ConversationSession session = Build(PolicyYaml, reply, fill, auditSink: sink).Create("conversation-1");

            _ = await session.RunTurnAsync("goodbye", TestContext.Current.CancellationToken);

            Assert.True(session.IsComplete);
            AuditEvent ended = Assert.Single(await session.RowsAsync(sink), item => item.Kind == AuditEventKind.ConversationEnded);
            Assert.Null(ended.TurnIndex);

            // The reason is one token of the closed set, so a report can count it. The terminal stage is
            // detail beside it, and it is not the reason.
            Assert.Equal("agent.completed", ended.Payload[AuditPayloadKeys.EndReason]);
            Assert.Equal("close", ended.Payload[AuditPayloadKeys.StageAfter]);

            // A hang-up frame that arrives after the machine already closed the conversation writes nothing.
            Assert.False(session.EndConversation(ConversationEndReason.CallerHungUp));
        }

        // No row of a conversation lands after its conversation.ended. The turn that reaches the terminal stage
        // writes its rows first, so the end is dated once they landed.
        [Fact]
        public async Task ATerminalStage_DatesTheEndAfterTheTurnsRowsLanded()
        {
            using SequencedChatClient reply = new("goodbye.");
            using SequencedChatClient fill = new(SaidGoodbye);
            InMemoryAuditSink sink = new();
            TestTimeProvider clock = new();
            SlowAppends store = new(new InMemoryConversationStore(), clock, TimeSpan.FromSeconds(3));
            ConversationSession session = Build(PolicyYaml, reply, fill, timeProvider: clock, auditSink: sink, store: store)
                .Create("conversation-1");

            TurnResult turn = await session.RunTurnAsync("goodbye", TestContext.Current.CancellationToken);

            Assert.True(turn.IsTerminal);
            AuditEvent ended = Assert.Single(await session.RowsAsync(sink), item => item.Kind == AuditEventKind.ConversationEnded);
            Assert.NotNull(store.LandedAt);
            Assert.True(
                ended.OccurredAt >= store.LandedAt,
                $"conversation.ended is dated {ended.OccurredAt:O}, before the turn's rows landed at {store.LandedAt:O}.");
        }

        // A conversation that a terminal stage ended stays ended when it is loaded again. The turn it
        // refuses, and a host end that is then not a first end, write no row after conversation.ended.
        [Fact]
        public async Task AReloadedEndedConversation_WritesNoRowAfterItsEnd()
        {
            using SequencedChatClient reply = new("goodbye.");
            using SequencedChatClient fill = new(SaidGoodbye);
            InMemoryAuditSink sink = new();
            ConversationSessionFactory factory = Build(PolicyYaml, reply, fill, auditSink: sink, store: new InMemoryConversationStore());
            ConversationSession first = factory.Create("conversation-1");
            _ = await first.RunTurnAsync("goodbye", TestContext.Current.CancellationToken);
            await first.FlushTranscriptAsync();
            await first.DisposeAsync();

            ConversationSession second = factory.Create("conversation-1");
            _ = await Assert.ThrowsAsync<InvalidOperationException>(
                () => second.RunTurnAsync("are you there?", TestContext.Current.CancellationToken));
            Assert.False(second.EndConversation(ConversationEndReason.CallerHungUp));

            IReadOnlyList<AuditEvent> rows = await second.RowsAsync(sink);
            Assert.Equal(
                [AuditEventKind.ConversationStarted, AuditEventKind.TurnCompleted, AuditEventKind.ConversationEnded],
                rows.Select(row => row.Kind));
        }

        // TurnResult.EndedAt: every row of a turn carries the moment it ended, not the moment its words landed.
        [Fact]
        public async Task ATurnRow_IsDatedWhenTheTurnEnded_NotWhenItsWordsLanded()
        {
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(StayingNull);
            InMemoryAuditSink sink = new();
            TestTimeProvider clock = new();
            SlowAppends store = new(new InMemoryConversationStore(), clock, TimeSpan.FromSeconds(3));
            ConversationSession session = Build(PolicyYaml, reply, fill, timeProvider: clock, auditSink: sink, store: store)
                .Create("conversation-1");

            TurnResult turn = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            AuditEvent completed = Assert.Single(await session.RowsAsync(sink), item => item.Kind == AuditEventKind.TurnCompleted);
            Assert.True(store.LandedAt > turn.EndedAt);
            Assert.Equal(turn.EndedAt, completed.OccurredAt);
        }

        [Fact]
        public async Task AHostThatEndsTheConversation_ClosesTheChainOnce()
        {
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(StayingNull);
            InMemoryAuditSink sink = new();
            ConversationSession session = Build(PolicyYaml, reply, fill, auditSink: sink).Create("conversation-1");

            Assert.True(session.EndConversation(ConversationEndReason.CallerHungUp));
            Assert.False(session.EndConversation(ConversationEndReason.CallerHungUp));

            IReadOnlyList<AuditEvent> rows = await session.RowsAsync(sink);
            Assert.Equal([AuditEventKind.ConversationStarted, AuditEventKind.ConversationEnded], rows.Select(item => item.Kind));
            AuditEvent ended = rows[1];
            Assert.Equal("caller.hangup", ended.Payload[AuditPayloadKeys.EndReason]);

            // The machine never ran, so no stage closed the conversation and no stage rides on the event.
            Assert.False(ended.Payload.ContainsKey(AuditPayloadKeys.StageAfter));
            Assert.True(session.IsComplete);
        }

        /// <summary>The conversation goes to a human through the conference pattern.</summary>
        [Fact]
        public async Task ATransferToAHuman_ClosesTheChainWithItsOwnReason()
        {
            using SequencedChatClient reply = new("one moment please.");
            using SequencedChatClient fill = new(StayingNull);
            InMemoryAuditSink sink = new();
            ConversationSession session = Build(PolicyYaml, reply, fill, auditSink: sink).Create("conversation-1");

            // The adapter joins the conversation to a conference and never sends the transfer command.
            Assert.True(session.EndConversation(ConversationEndReason.TransferredToHuman));

            IReadOnlyList<AuditEvent> rows = await session.RowsAsync(sink);
            Assert.Equal([AuditEventKind.ConversationStarted, AuditEventKind.ConversationEnded], rows.Select(item => item.Kind));
            AuditEvent ended = rows[1];
            Assert.Equal("agent.transferred", ended.Payload[AuditPayloadKeys.EndReason]);
        }

        [Fact]
        public async Task AReasonOutsideTheClosedSet_EndsNoConversation()
        {
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(StayingNull);
            InMemoryAuditSink sink = new();
            ConversationSession session = Build(PolicyYaml, reply, fill, auditSink: sink).Create("conversation-1");

            _ = Assert.Throws<ArgumentOutOfRangeException>(() => session.EndConversation((ConversationEndReason)99));

            // Nothing moved. The conversation still runs, and the chain still holds only its first event.
            Assert.False(session.IsComplete);
            Assert.DoesNotContain(await session.RowsAsync(sink), item => item.Kind == AuditEventKind.ConversationEnded);
        }

        /// <summary>A store whose appends take time on the test's clock, and that notes when the last one landed.</summary>
        private sealed class SlowAppends(IConversationStore inner, TestTimeProvider clock, TimeSpan takes) : DelegatingConversationStore(inner)
        {
            public DateTimeOffset? LandedAt { get; private set; }

            public override async ValueTask<IReadOnlyList<ConversationMessage>> AppendAsync(
                string conversationId,
                IReadOnlyList<ConversationMessageDraft> messages,
                ConversationSessionState? state = null,
                CancellationToken cancellationToken = default)
            {
                IReadOnlyList<ConversationMessage> rows = await Inner.AppendAsync(conversationId, messages, state, cancellationToken);
                clock.Advance(takes);
                LandedAt = clock.GetUtcNow();
                return rows;
            }
        }
    }
}
