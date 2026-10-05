using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Domain;
using AgentCore.Domain.Audit;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;
using AgentCore.Application.Runtime.Turn.Lifecycle;

namespace AgentCore.Application.Tests.Hooks
{
    public sealed class ConversationEndNoticeTests
    {
        private const string StagedToolYaml = """
        apiVersion: agentcore/v1
        guards:
          never: { ">=": [ { var: turnIndex }, 99 ] }
        tools:
          - { id: transfer, kind: builtin, uses: orders.read, description: "Put the caller through to a person." }
        agents:
          defaults: { clock: false }
          items:
            - { id: only, instructions: "ok", tools: [ transfer ] }
        entries:
          main:
            policy:
              initial: working
              stages:
                - { id: working, agent: only, to: [ { stage: done, when: never } ] }
                - { id: done, agent: only, terminal: true }
        """;

        private const string EndsAtOnceYaml = """
        apiVersion: agentcore/v1
        agents:
          defaults: { clock: false }
          items:
            - { id: only, instructions: "ok" }
        entries:
          main:
            policy:
              initial: done
              stages:
                - { id: done, agent: only, terminal: true }
        """;

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact]
        public async Task AnEndWithNoTurnRunningIsRaisedAtOnceAndLast()
        {
            RecordingHook hook = new();
            using ScriptedChatClient reply = new("hello");
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, reply, [hook]);
            _ = await session.RunTurnAsync("hi", Ct);

            Assert.True(session.EndConversation(ConversationEndReason.CallerHungUp));
            _ = await Assert.ThrowsAsync<InvalidOperationException>(() => session.RunTurnAsync("again", Ct));
            await session.FlushNoticesAsync();

            ConversationEnded ended = Assert.Single(hook.Of<ConversationEnded>());
            Assert.Equal(ConversationEndReason.CallerHungUp, ended.Reason);
            Assert.Null(ended.TerminalStage);
            Assert.True(hook.Of<TurnCompleted>().Single().Scope.Sequence < ended.Scope.Sequence);
            TurnRefused refused = Assert.Single(hook.Of<TurnRefused>());
            Assert.Equal(TurnRefusal.Terminal, refused.Reason);
            Assert.True(refused.AfterEnd);
        }

        // A reloaded conversation that an earlier session ended has its end in the chain already, so a notice
        // that must follow the end is raised at once instead of waiting for an end that never comes.
        [Fact]
        public async Task ANoticeThatFollowsTheEndIsRaisedAtOnceOnAReloadedEndedConversation()
        {
            InMemoryConversationStore store = new();
            using ScriptedChatClient reply = new("hello");
            ConversationSession first = HookSessions.Create(EndsAtOnceYaml, reply, store: store, conversationId: "conversation-1");
            _ = await first.RunTurnAsync("hi", Ct);
            await first.FlushTranscriptAsync();
            await first.DisposeAsync();

            RecordingHook hook = new();
            ConversationSession second = HookSessions.Create(EndsAtOnceYaml, reply, [hook], store, conversationId: "conversation-1");
            _ = await Assert.ThrowsAsync<InvalidOperationException>(() => second.RunTurnAsync("are you there?", Ct));
            second.Lifetime.Ending.RaiseAfterEnd(new CallEnded(second.Hooks.Scope(turnIndex: null, stage: null), "call-1", 1, Cause: null, CallEndReason.Ended));
            await second.FlushNoticesAsync();

            Assert.Equal("call-1", Assert.Single(hook.Of<CallEnded>()).CallId);
        }

        // A session that ends before it opened its store still starts first, and says it never looked.
        [Fact]
        public async Task AnEndBeforeAnyTurnRaisesAnUnopenedStartFirst()
        {
            RecordingHook hook = new();
            using ScriptedChatClient reply = new("hello");
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, reply, [hook]);

            Assert.True(session.EndConversation(ConversationEndReason.CallerHungUp));
            await session.FlushNoticesAsync();

            Assert.Equal([typeof(ConversationStarted), typeof(ConversationEnded)], hook.Notices.Select(notice => notice.GetType()));
            Assert.Equal(ConversationOrigin.Unopened, hook.Of<ConversationStarted>().Single().Origin);
            Assert.True(hook.Notices[0].Scope.Sequence < hook.Notices[1].Scope.Sequence);
        }

        // A tool ends the conversation inside its own turn; the turn finishes first.
        [Fact]
        public async Task AnEndCalledInsideATurnIsRaisedAfterThatTurnCompletes()
        {
            RecordingHook hook = new();
            ConversationSession? session = null;
            ToolCallingChatClient model = new("goodbye");
            session = HookSessions.Create(
                HookSessions.ToolAgentYaml,
                model,
                [hook],
                tools: tool => AIFunctionFactory.Create(
                    () =>
                    {
                        _ = session!.EndConversation(ConversationEndReason.TransferredToHuman);
                        return "transferred";
                    },
                    tool.Id,
                    tool.Description ?? tool.Id));

            TurnResult turn = await session.RunTurnAsync("put me through", Ct);
            await session.FlushNoticesAsync();

            Assert.Equal("goodbye", turn.ReplyText);
            Assert.Equal(
                [typeof(TurnCompleted), typeof(ConversationEnded)],
                hook.Notices.Where(n => n is TurnCompleted or ConversationEnded).Select(n => n.GetType()));
            Assert.Equal(ConversationEndReason.TransferredToHuman, hook.Of<ConversationEnded>().Single().Reason);
        }

        // An end that waits on a turn waits 5 s, then is raised; the late seal still gets through.
        [Fact(Timeout = 30_000)]
        public async Task AnEndWaitsFiveSecondsForTheRunningTurnAndItsLateSealStillGetsThrough()
        {
            FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
            RecordingHook hook = new();
            using ScriptedChatClient reply = new("Hello", " there") { GateAfterFirstFragment = true };
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, reply, [hook], time: time);

            await using IAsyncEnumerator<ChatResponseUpdate> stream = session.RunTurnStreamingAsync("hi", Ct).GetAsyncEnumerator(Ct);
            Assert.True(await stream.MoveNextAsync());

            _ = session.EndConversation(ConversationEndReason.CallerHungUp);
            await session.FlushNoticesAsync();
            Assert.Empty(hook.Of<ConversationEnded>());

            await time.WaitForTimersAsync(time.GetUtcNow() + ConversationEnding.TurnWait, 1);
            time.Advance(ConversationEnding.TurnWait);
            _ = await hook.WaitForAsync<ConversationEnded>().WaitAsync(TimeSpan.FromSeconds(10), Ct);

            reply.OpenGate();
            while (await stream.MoveNextAsync())
            {
            }

            await session.FlushNoticesAsync();
            Assert.Equal(
                [typeof(ConversationEnded), typeof(TurnCompleted)],
                hook.Notices.Where(n => n is TurnCompleted or ConversationEnded).Select(n => n.GetType()));
        }

        // A turn dropped unread never seals, so the end goes out when it frees the conversation, not 5 s later.
        [Fact(Timeout = 30_000)]
        public async Task AnEndWaitingOnATurnThatNeverSealsIsRaisedWhenTheTurnLetsGo()
        {
            FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
            RecordingHook hook = new();
            using ScriptedChatClient reply = new("hello");
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, reply, [hook], time: time);
            TurnRun unread = await session.StartTurnAsync(new ChatMessage(ChatRole.User, "hi"), origin: null, Ct);

            _ = session.EndConversation(ConversationEndReason.CallerHungUp);
            await unread.DisposeAsync();

            ConversationEnded ended = await hook.WaitForAsync<ConversationEnded>().WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.Equal(ConversationEndReason.CallerHungUp, ended.Reason);
            Assert.Equal(DateTimeOffset.UnixEpoch, ended.Scope.OccurredAt);
        }

        // Once a tool ended a staged conversation, the seal of its turn, which left the machine in a
        // stage that is not terminal, does not open the conversation again. The next turn is refused after the end.
        [Fact]
        public async Task ATurnAfterAToolEndedAStagedConversationIsRefusedAfterTheEnd()
        {
            RecordingHook hook = new();
            ConversationSession? session = null;
            ToolCallingChatClient model = new("goodbye");
            session = HookSessions.Create(
                StagedToolYaml,
                model,
                [hook],
                tools: tool => AIFunctionFactory.Create(
                    () =>
                    {
                        _ = session!.EndConversation(ConversationEndReason.TransferredToHuman);
                        return "transferred";
                    },
                    tool.Id,
                    tool.Description ?? tool.Id));

            TurnResult turn = await session.RunTurnAsync("put me through", Ct);

            Assert.True(turn.IsTerminal);
            Assert.True(session.IsComplete);
            _ = await Assert.ThrowsAsync<InvalidOperationException>(() => session.RunTurnAsync("are you there?", Ct));
            await session.FlushNoticesAsync();

            _ = Assert.Single(hook.Of<TurnCompleted>());
            TurnRefused refused = Assert.Single(hook.Of<TurnRefused>());
            Assert.Equal(TurnRefusal.Terminal, refused.Reason);
            Assert.True(refused.AfterEnd);
        }

        // A turn that waited for the conversation while the host ended it is not admitted once the
        // running turn of a staged conversation seals.
        [Fact(Timeout = 30_000)]
        public async Task ATurnWaitingWhileTheHostEndsAStagedConversationIsRefusedAfterTheEnd()
        {
            FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
            RecordingHook hook = new();
            using ScriptedChatClient reply = new("Hello", " there") { GateAfterFirstFragment = true };
            ConversationSession session = HookSessions.Create(StagedToolYaml, reply, [hook], time: time);

            await using IAsyncEnumerator<ChatResponseUpdate> stream = session.RunTurnStreamingAsync("hi", Ct).GetAsyncEnumerator(Ct);
            Assert.True(await stream.MoveNextAsync());
            Task<TurnResult> waiting = session.RunTurnAsync("hello?", Ct);
            await time.WaitForTimersAsync(time.GetUtcNow() + ConversationBusyMark.WaitLimit, 1);

            Assert.True(session.EndConversation(ConversationEndReason.CallerHungUp));
            reply.OpenGate();
            while (await stream.MoveNextAsync())
            {
            }

            _ = await Assert.ThrowsAsync<InvalidOperationException>(() => waiting);
            await session.FlushNoticesAsync();

            _ = Assert.Single(hook.Of<TurnCompleted>());
            Assert.True(Assert.Single(hook.Of<TurnRefused>()).AfterEnd);
        }

        // Terminal stage: the end comes from the seal, with the stage the machine stopped in.
        [Fact]
        public async Task ATerminalStageEndsTheConversationWithItsStage()
        {
            RecordingHook hook = new();
            using ScriptedChatClient reply = new("done");
            ConversationSession session = HookSessions.Create(HookSessions.EndsAfterTheFirstTurnYaml, reply, [hook]);

            _ = await session.RunTurnAsync("hi", Ct);
            await session.FlushNoticesAsync();

            ConversationEnded ended = Assert.Single(hook.Of<ConversationEnded>());
            Assert.Equal(ConversationEndReason.AgentCompleted, ended.Reason);
            Assert.Equal("done", ended.TerminalStage);
        }
    }
}
