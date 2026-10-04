using AgentCore.Application.Configuration.Schema;
using AgentCore.Domain;
using Xunit;
using AgentCore.Application.Runtime.Session;
using static AgentCore.Application.Tests.Runtime.ConversationSessionTestSupport;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>The reserved state slots, and what a completed or policy-less conversation allows.</summary>
    public sealed class ConversationSessionLifecycleTests
    {
        // The reserved slots.
        [Fact]
        public async Task TheReservedSlots_CountTheFinishedTurnsAndReadTheInjectedClock()
        {
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(StayingNull);
            TestTimeProvider clock = new();
            ConversationSession session = Build(PolicyYaml, reply, fill, timeProvider: clock).Create();

            Assert.Equal(0, session.State.TurnIndex);
            Assert.Equal(0, session.State.ConversationDurationSeconds);

            clock.Advance(TimeSpan.FromSeconds(12.5));
            TurnResult first = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            Assert.Equal(0, first.TurnIndex);
            Assert.Equal(1, session.State.TurnIndex);
            Assert.Equal(12.5, session.State.ConversationDurationSeconds);

            clock.Advance(TimeSpan.FromSeconds(7.5));
            TurnResult second = await session.RunTurnAsync("still there?", TestContext.Current.CancellationToken);

            Assert.Equal(1, second.TurnIndex);
            Assert.Equal(2, session.State.TurnIndex);
            Assert.Equal(20, session.State.ConversationDurationSeconds);
        }

        [Fact]
        public async Task TheStage_ReachesTheStateDocumentSoAGuardReadsIt()
        {
            using SequencedChatClient reply = new("first reply.");
            ConversationSession session = Build(TwoStagesYaml, reply, null).Create();

            _ = await session.RunTurnAsync("one", TestContext.Current.CancellationToken);

            Assert.Equal("close", session.Stage);
            Assert.Equal("close", session.State.Stage);
            Assert.Equal("close", session.State.Snapshot()[ReservedStateSlots.Stage]!.GetValue<string>());
        }

        // A finished conversation.
        [Fact]
        public async Task ACompleteConversation_RejectsAFurtherTurn()
        {
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(SaidGoodbye);
            ConversationSession session = Build(PolicyYaml, reply, fill).Create();

            _ = await session.RunTurnAsync("goodbye", TestContext.Current.CancellationToken);
            Assert.True(session.IsComplete);

            InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.RunTurnAsync("hello again", TestContext.Current.CancellationToken));

            Assert.Contains("close", failure.Message, StringComparison.Ordinal);
            Assert.Equal(1, reply.Calls);
        }

        [Fact]
        public async Task ADocumentWithNoPolicy_RunsTheOneAgentAndNeverEnds()
        {
            using SequencedChatClient reply = new("I answered.");
            ConversationSession session = Build(OneAgentYaml, reply, null).Create();

            TurnResult turn = await session.RunTurnAsync("hello", TestContext.Current.CancellationToken);

            Assert.Equal(string.Empty, session.Stage);
            Assert.Equal(string.Empty, turn.StageBefore);
            Assert.Equal(string.Empty, turn.StageAfter);
            Assert.False(turn.IsTerminal);
            Assert.False(session.IsComplete);

            _ = await session.RunTurnAsync("again", TestContext.Current.CancellationToken);
            Assert.Equal(2, reply.Calls);
        }
    }
}
