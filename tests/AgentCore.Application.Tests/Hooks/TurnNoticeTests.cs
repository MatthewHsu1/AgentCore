using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Runtime;
using AgentCore.Domain.Audit;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Runtime.Turn.Lifecycle;

namespace AgentCore.Application.Tests.Hooks
{
    public sealed class TurnNoticeTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // A turn starts, then completes, with the caller's and the reply's words.
        [Fact]
        public async Task AnAnsweredTurnStartsThenCompletes()
        {
            RecordingHook hook = new();
            using ScriptedChatClient reply = new("hello", " there");
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, reply, [hook]);

            _ = await session.RunTurnAsync("hi", Ct);
            await session.FlushNoticesAsync();

            TurnStarted started = Assert.Single(hook.Of<TurnStarted>());
            TurnCompleted completed = Assert.Single(hook.Of<TurnCompleted>());
            Assert.Equal("only", started.AgentId);
            Assert.Equal("hi", started.UserText);
            Assert.Equal(0, started.Scope.TurnIndex);
            Assert.Equal(TurnOutcome.Answered, completed.Outcome);
            Assert.Equal("hi", completed.UserText);
            Assert.Equal("hello there", completed.ReplyText);
            Assert.Null(completed.Failure);
            Assert.False(completed.FailedInTool);
            Assert.True(started.Scope.Sequence < completed.Scope.Sequence);
        }

        // A tool that spent its budget makes a Fallback turn that says the tool failed.
        [Fact]
        public async Task AToolThatSpentItsBudgetIsAFallbackTurnFailedInTheTool()
        {
            RecordingHook hook = new();
            using LoopingToolCallingChatClient reply = new();
            ConversationSession session = HookSessions.Create(
                HookSessions.ToolAgentYaml, reply, [hook], tools: new ThrowingToolBuilder().Create);

            _ = await session.RunTurnAsync("where is my order", Ct);
            await session.FlushNoticesAsync();

            TurnCompleted completed = Assert.Single(hook.Of<TurnCompleted>());
            Assert.Equal(TurnOutcome.Fallback, completed.Outcome);
            Assert.True(completed.FailedInTool);
            Assert.Contains(ThrowingToolBuilder.Message, completed.Cause?.Message, StringComparison.Ordinal);
        }

        // No text is an Empty turn that spoke the fallback.
        [Fact]
        public async Task AQuietRunIsAnEmptyTurn()
        {
            RecordingHook hook = new();
            using ScriptedChatClient reply = new("   ");
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, reply, [hook]);

            _ = await session.RunTurnAsync("hi", Ct);
            await session.FlushNoticesAsync();

            TurnCompleted completed = Assert.Single(hook.Of<TurnCompleted>());
            Assert.Equal(TurnOutcome.Empty, completed.Outcome);
            Assert.Equal(TurnFailureReasons.EmptyReply, completed.Failure);
        }

        // Terminal refusals raised nothing).
        [Fact]
        public async Task AStageMoveIsNamedAndATurnOnATerminalConversationIsRefused()
        {
            RecordingHook hook = new();
            using ScriptedChatClient reply = new("done");
            ConversationSession session = HookSessions.Create(HookSessions.EndsAfterTheFirstTurnYaml, reply, [hook]);

            _ = await session.RunTurnAsync("hi", Ct);
            _ = await Assert.ThrowsAsync<InvalidOperationException>(() => session.RunTurnAsync("again", Ct));
            await session.FlushNoticesAsync();

            StageChanged moved = Assert.Single(hook.Of<StageChanged>());
            Assert.Equal(("working", "done"), (moved.Before, moved.After));
            TurnRefused refused = Assert.Single(hook.Of<TurnRefused>());
            Assert.Equal(TurnRefusal.Terminal, refused.Reason);
            Assert.True(refused.AfterEnd);
        }

        // The end waits for the running turn, but a refusal in that wait already says the conversation ended.
        [Fact]
        public async Task ARefusalWhileTheEndWaitsForTheTurnSaysSo()
        {
            RecordingHook hook = new();
            using ScriptedChatClient reply = new("Hello", " there") { GateAfterFirstFragment = true };
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, reply, [hook]);
            await using IAsyncEnumerator<ChatResponseUpdate> stream = session.RunTurnStreamingAsync("hi", Ct).GetAsyncEnumerator(Ct);
            Assert.True(await stream.MoveNextAsync());

            _ = session.EndConversation(ConversationEndReason.CallerHungUp);
            _ = await Assert.ThrowsAsync<InvalidOperationException>(() => session.RunTurnAsync("again", Ct));
            await session.FlushNoticesAsync();

            Assert.Empty(hook.Of<ConversationEnded>());
            TurnRefused refused = Assert.Single(hook.Of<TurnRefused>());
            Assert.Equal(TurnRefusal.Terminal, refused.Reason);
            Assert.True(refused.AfterEnd);

            reply.OpenGate();
            while (await stream.MoveNextAsync())
            {
            }
        }

        // Values copied from ConversationSessionEditTests.AnEdit_NamesTheTurnsItWithdrew.
        [Fact]
        public async Task AnEditNamesTheTurnsItWithdrew()
        {
            RecordingHook hook = new();
            using ScriptedChatClient reply = new("an answer.");
            ConversationSession session = HookSessions.Create(
                HookSessions.OneAgentYaml, reply, [hook], new InMemoryConversationStore(), conversationId: "conversation-1");

            _ = await session.RunTurnAtOriginAsync("q1", new ConversationTurnOrigin("caller-1", null) { NamesParent = true }, Ct);
            string? firstReply = session.LastReplyMessageId;
            _ = await session.RunTurnAsync("q2", Ct);
            _ = await session.RunTurnAsync("q3", Ct);
            _ = await session.RunTurnAtOriginAsync("q2, rewritten", new ConversationTurnOrigin("caller-4", firstReply) { NamesParent = true }, Ct);
            await session.FlushNoticesAsync();

            TurnSuperseded superseded = Assert.Single(hook.Of<TurnSuperseded>());
            Assert.Equal((1, 2), (superseded.WithdrewFrom, superseded.WithdrewThrough));
            Assert.Equal(3, superseded.Scope.TurnIndex);
        }

        // Each output update that leaves the turn.
        [Fact]
        public async Task EachUpdateTheCallerGetsIsANotice()
        {
            RecordingHook hook = new();
            using ScriptedChatClient reply = new("hello", " there");
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, reply, [hook]);

            List<string> got = [];
            await foreach (ChatResponseUpdate update in session.RunTurnStreamingAsync("hi", Ct))
            {
                got.Add(update.Text);
            }

            await session.FlushNoticesAsync();

            Assert.Equal(got.Where(text => text.Length > 0), hook.Of<ReplyUpdated>().Select(notice => notice.Text));
            Assert.Equal(["hello", " there"], hook.Of<ReplyUpdated>().Select(notice => notice.Text));
        }
    }
}
