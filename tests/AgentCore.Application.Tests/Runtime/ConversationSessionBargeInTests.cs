using AgentCore.Application.Tests.Fakes;
using AgentCore.Domain;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Cut;
using AgentCore.Application.Runtime.Session;
using static AgentCore.Application.Tests.Runtime.ConversationSessionTestSupport;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>Barge-in. The record holds what the caller heard.</summary>
    public sealed class ConversationSessionBargeInTests
    {
        [Fact]
        public async Task AnInterruption_RecordsTheHeardTextAndTheDurationTheRelayReported()
        {
            using ScriptedChatClient reply = new("I can help with that,", " and here is the long part.")
            {
                GateAfterFirstFragment = true,
            };
            using SequencedChatClient fill = new(StayingNull);
            ConversationSession session = Build(PolicyYaml, reply, fill).Create();

            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                timeout.Token, TestContext.Current.CancellationToken);

            await using IAsyncEnumerator<ChatResponseUpdate> updates = session.RunTurnStreamingAsync("hi", linked.Token).GetAsyncEnumerator(linked.Token);
            Assert.True(await updates.MoveNextAsync());
            Assert.Equal("I can help with that,", updates.Current.Text);

            // Both values are reported on the interrupt frame, at 1 ms. Nothing estimates either.
            Assert.True(session.Cut(0, new TurnCut("I can help", TimeSpan.FromMilliseconds(740))));
            Assert.False(await updates.MoveNextAsync());

            Assert.NotNull(session.LastTurn);
            Assert.Equal("I can help", session.LastTurn.ReplyText);
            Assert.Equal(TimeSpan.FromMilliseconds(740), session.LastTurn.Cut);
            Assert.Null(session.LastTurn.Failure);

            // The transcript holds the text the caller heard, not the text the model produced.
            Assert.Equal("I can help", session.Transcript[^1].Text);
            Assert.DoesNotContain(session.Transcript, message => Contains(message, "long part"));
        }

        [Fact]
        public async Task AnInterruption_RunsEveryWriterAndLeavesTheSessionReady()
        {
            using ScriptedChatClient reply = new("hello", " there.") { GateAfterFirstFragment = true };
            using SequencedChatClient fill = new(StayingNull);
            ConversationSession session = Build(PolicyYaml, reply, fill).Create();

            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                timeout.Token, TestContext.Current.CancellationToken);

            await using (IAsyncEnumerator<ChatResponseUpdate> updates = session.RunTurnStreamingAsync("hi", linked.Token).GetAsyncEnumerator(linked.Token))
            {
                Assert.True(await updates.MoveNextAsync());
                Assert.True(session.Cut(0, new TurnCut("hel", TimeSpan.FromMilliseconds(120))));

                while (await updates.MoveNextAsync())
                {
                    // The stream ends on the interruption, so nothing arrives here.
                }
            }

            // The writers ran in their fixed order, and the counter read the stage the turn spoke in.
            Assert.Equal(1, session.State.TurnIndex);
            Assert.Equal(1, session.State.Read("greetingTurns")!.GetValue<long>());
            Assert.Equal("greeting", session.Stage);

            // The session is not locked, so the caller who interrupted can speak next.
            reply.OpenGate();
            TurnResult next = await session.RunTurnAsync("go on", linked.Token);
            Assert.Equal(1, next.TurnIndex);
            Assert.Null(next.Cut);
        }

        // A cut of a buffered turn that names nothing shown keeps its user message only,
        // and the turn before it stays as it was.
        [Fact]
        public async Task AnInterruptionDuringABufferedTurn_CutsThatTurnWithNothingShown_AndLeavesThePreviousTurn()
        {
            using ScriptedChatClient reply = new("hello", " there.");
            using SequencedChatClient fill = new(StayingNull);
            ConversationSession session = Build(PolicyYaml, reply, fill).Create();

            TurnResult first = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);
            Assert.Equal("hello there.", first.ReplyText);

            reply.GateAfterFirstFragment = true;
            Task<TurnResult> running = session.RunTurnAsync("go on", TestContext.Current.CancellationToken);
            Assert.True(session.Cut(1, new TurnCut(string.Empty, TimeSpan.FromMilliseconds(90))));

            TurnResult second = await running;

            Assert.Equal(1, second.TurnIndex);
            Assert.Equal(string.Empty, second.ReplyText);
            _ = Assert.NotNull(second.Cut);
            Assert.Equal(["hi", "hello there.", "go on"], session.Transcript.Select(message => message.Text));
            Assert.Equal(2, session.State.TurnIndex);
        }

        [Fact]
        public void AnInterruptionWithNoRunningTurn_ReportsFalseAndChangesNothing()
        {
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(StayingNull);
            ConversationSession session = Build(PolicyYaml, reply, fill).Create();

            // A frame that arrives after the turn ended must not drop the conversation, so it answers false.
            Assert.False(session.Cut(0, new TurnCut("nothing played", TimeSpan.FromMilliseconds(5))));

            Assert.Null(session.LastTurn);
            Assert.Equal(0, session.State.TurnIndex);
            Assert.Empty(session.Transcript);
        }
    }
}
