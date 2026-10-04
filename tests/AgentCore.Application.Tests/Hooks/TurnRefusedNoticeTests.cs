using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Conversation;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions.Memory;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Transcript;
using AgentCore.Domain;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;
using AgentCore.Application.Runtime.Turn.Lifecycle;

namespace AgentCore.Application.Tests.Hooks
{
    public sealed class TurnRefusedNoticeTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // A disposed session's refusal raised nothing.
        [Fact]
        public async Task ATurnOnADisposedSessionIsRefusedAsDisposed()
        {
            RecordingHook hook = new();
            using ScriptedChatClient reply = new("hello");
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, reply, [hook]);
            _ = await session.RunTurnAsync("hi", Ct);
            await session.DisposeAsync();

            _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => session.RunTurnAsync("again", Ct));
            await session.FlushNoticesAsync();

            Assert.Equal(TurnRefusal.Disposed, Assert.Single(hook.Of<TurnRefused>()).Reason);
        }

        // in_use is the refusal of a conversation another entry holds; it reaches the holder's hooks.
        [Fact]
        public async Task AConversationAnotherEntryHoldsIsRefusedAsInUse()
        {
            RecordingHook hook = new();
            using ScriptedChatClient reply = new("hello");
            IReadOnlyDictionary<string, CompiledAgent> compiled = HookSessions.Compile(HookSessions.TwoEntryYaml, reply, [hook]);
            Dictionary<string, IConversationSessionFactory> factories = new(StringComparer.Ordinal)
            {
                ["main"] = new ConversationSessionFactory(compiled["main"], new GuardEvaluator(compiled["main"].Configuration.Guards)),
                ["other"] = new ConversationSessionFactory(compiled["other"], new GuardEvaluator(compiled["other"].Configuration.Guards)),
            };
            using InMemoryConversationSessions sessions = new(factories, InMemoryConversationSessions.DefaultIdleTimeout, TimeProvider.System);

            ConversationSession held = await sessions.GetOrOpenAsync("main", "c1", state: null, Ct);
            _ = await Assert.ThrowsAsync<ConversationInUseException>(() => sessions.GetOrOpenAsync("other", "c1", state: null, Ct).AsTask());
            await held.FlushNoticesAsync();

            TurnRefused refused = Assert.Single(hook.Of<TurnRefused>());
            Assert.Equal(TurnRefusal.InUse, refused.Reason);
            Assert.Equal("c1", refused.Scope.ConversationId);
        }

        // A turn the store refused committed nothing, so it moved no stage: only the session that saved the turn says so.
        [Fact(Timeout = 30_000)]
        public async Task AStoreRefusedTurnRaisesNoStageMove()
        {
            RecordingHook hook = new();
            HeldFirstTurnChatClient reply = new("I am Alice");
            CompiledAgent compiled = HookSessions.Compile(
                HookSessions.EndsAfterTheFirstTurnYaml,
                reply,
                [hook],
                new ConversationSessionRefusedCatchUpTests.UnmarkableStore(new InMemoryConversationStore()))["main"];
            ConversationSessionFactory factory = new(compiled, new GuardEvaluator(compiled.Configuration.Guards));
            ConversationSession a = factory.Create("c1");
            ConversationSession b = factory.Create("c1");

            Task<TurnResult> alice = a.RunTurnAsync("I am Alice", Ct);
            await reply.Held.Task.WaitAsync(Ct);
            _ = await b.RunTurnAsync("I am Bob", Ct);
            reply.Release();
            _ = await Assert.ThrowsAsync<ConversationTurnConflictException>(() => alice);
            await a.FlushNoticesAsync();

            Assert.Equal(TurnRefusal.Conflict, Assert.Single(hook.Of<TurnRefused>()).Reason);
            StageChanged moved = Assert.Single(hook.Of<StageChanged>());
            Assert.Equal(b.Hooks.SessionId, moved.Scope.SessionId);
            Assert.True(moved.Scope.Sequence < hook.Of<TurnCompleted>().Single(completed => completed.Scope.SessionId == b.Hooks.SessionId).Scope.Sequence);
        }

        // AbandonRun (ConversationTurnStream.cs:239-251): a run disposed unread is refused as dropped, with its index.
        [Fact]
        public async Task ARunDisposedUnreadIsRefusedAsDropped()
        {
            RecordingHook hook = new();
            using ScriptedChatClient reply = new("hello");
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, reply, [hook]);

            TurnRun run = await session.StartTurnAsync(new ChatMessage(ChatRole.User, "hi"), origin: null, Ct);
            await run.DisposeAsync();
            await session.FlushNoticesAsync();

            TurnRefused refused = Assert.Single(hook.Of<TurnRefused>());
            Assert.Equal(TurnRefusal.Dropped, refused.Reason);
            Assert.False(refused.AfterEnd);
            Assert.Equal(0, refused.Scope.TurnIndex);
        }
    }
}
