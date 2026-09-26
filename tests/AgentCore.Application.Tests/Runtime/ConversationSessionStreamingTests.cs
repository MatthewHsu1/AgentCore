using AgentCore.Application.Runtime;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Domain;
using Microsoft.Extensions.AI;
using Xunit;
using static AgentCore.Application.Tests.Runtime.ConversationSessionTestSupport;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>The streaming path: what it carries while a turn runs, and which updates it drops.</summary>
    public sealed class ConversationSessionStreamingTests
    {
        // Streaming.
        [Fact]
        public async Task Streaming_CarriesTheWholeReplyAndThenFinishesTheTurn()
        {
            using ScriptedChatClient reply = new("hello", " there.");
            using SequencedChatClient fill = new(SaidGoodbye);
            ConversationSession session = Build(PolicyYaml, reply, fill).Create();

            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate update in session.RunTurnStreamingAsync("goodbye", TestContext.Current.CancellationToken))
            {
                updates.Add(update);
            }

            string text = string.Concat(updates.Select(update => update.Text));
            Assert.Equal("hello there.", text);

            Assert.NotNull(session.LastTurn);
            Assert.Equal("hello there.", session.LastTurn.ReplyText);
            Assert.Equal("close", session.LastTurn.StageAfter);
            Assert.True(session.IsComplete);
        }

        [Fact]
        public async Task Streaming_MovesNothingUntilTheEnumerationCompletes()
        {
            using ScriptedChatClient reply = new("hello", " there.") { GateAfterFirstFragment = true };
            using SequencedChatClient fill = new(SaidGoodbye);
            ConversationSession session = Build(PolicyYaml, reply, fill).Create();

            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                timeout.Token, TestContext.Current.CancellationToken);

            await using IAsyncEnumerator<ChatResponseUpdate> updates = session.RunTurnStreamingAsync("goodbye", linked.Token).GetAsyncEnumerator(linked.Token);

            // The model holds every fragment after the first, so this line runs mid-reply.
            Assert.True(await updates.MoveNextAsync());
            Assert.Equal("hello", updates.Current.Text);
            Assert.Null(session.LastTurn);
            Assert.Equal("greeting", session.Stage);
            Assert.Equal(0, session.State.TurnIndex);

            reply.OpenGate();
            while (await updates.MoveNextAsync())
            {
                // Drain the rest of the reply.
            }

            Assert.NotNull(session.LastTurn);
            Assert.Equal("close", session.Stage);
            Assert.Equal(1, session.State.TurnIndex);
        }

        // The state document takes no lock, so a second turn waits for the first instead of running beside it.
        [Fact]
        public async Task ASecondTurn_WaitsForTheFirstOne_ThenRunsAfterIt()
        {
            using ScriptedChatClient reply = new("hello", " there.") { GateAfterFirstFragment = true };
            using SequencedChatClient fill = new(StayingNull);
            ConversationSession session = Build(PolicyYaml, reply, fill).Create();

            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                timeout.Token, TestContext.Current.CancellationToken);

            await using IAsyncEnumerator<ChatResponseUpdate> updates = session.RunTurnStreamingAsync("hi", linked.Token).GetAsyncEnumerator(linked.Token);
            Assert.True(await updates.MoveNextAsync());

            Task<TurnResult> second = session.RunTurnAsync("hi again", linked.Token);
            await Task.Delay(TimeSpan.FromMilliseconds(200), linked.Token);
            Assert.False(second.IsCompleted);

            reply.OpenGate();
            while (await updates.MoveNextAsync())
            {
                // Drain the rest of the reply.
            }

            TurnResult after = await second;
            Assert.Equal(1, after.TurnIndex);
            Assert.Equal(2, session.State.TurnIndex);
        }

        // Section 8.6: the update stream carries content only.
        [Fact]
        public async Task Streaming_DropsEveryUpdateThatCarriesNoContent()
        {
            using LifecycleChatClient reply = new("hello", " there.");
            ConversationSession session = Build(TwoStagesYaml, reply, null).Create();

            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate update in session.RunTurnStreamingAsync("hi", TestContext.Current.CancellationToken))
            {
                updates.Add(update);
            }

            // AsAIAgent() yields 47 updates for 40 text fragments, and seven carry no content. The seam
            // filters them once, so no host writes the filter again. The trailing update is the turn's
            // committed ids (design section 6, step E5): it carries content but no text.
            Assert.Equal(6, reply.Yielded);
            Assert.Equal(
                ["hello", " there."],
                [.. updates.Where(update => !update.Contents.OfType<TurnCommittedContent>().Any()).Select(update => update.Text)]);
            Assert.NotNull(session.LastTurn);
            Assert.Equal("hello there.", session.LastTurn.ReplyText);
        }

        [Fact]
        public async Task Streaming_KeepsTheUpdateThatCarriesAToolCall()
        {
            using ToolCallingChatClient reply = new("your order is on the way.");
            using SequencedChatClient fill = new(StayingNull);
            ConversationSession session = Build(ToolYaml, reply, fill, new StubToolBuilder(/*lang=json,strict*/ """{ "status": "shipped" }""").Create).Create();

            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate update in session.RunTurnStreamingAsync(
                "where is my order", TestContext.Current.CancellationToken))
            {
                updates.Add(update);
            }

            // A tool call and a tool result are content a host may show, so the filter keeps them.
            Assert.Contains(updates, update => update.Contents.OfType<FunctionCallContent>().Any());
            Assert.Contains(updates, update => update.Text.Length > 0);
            Assert.DoesNotContain(updates, update => update.Contents.Count == 0);
        }
    }
}
