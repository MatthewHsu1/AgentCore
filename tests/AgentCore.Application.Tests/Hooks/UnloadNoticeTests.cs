using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions.Memory;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Domain.Audit;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;
using AgentCore.Application.Runtime.Turn.Lifecycle;

namespace AgentCore.Application.Tests.Hooks
{
    public sealed class UnloadNoticeTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact]
        public async Task ACloseUnloadsTheConversationAsClosed()
        {
            RecordingHook hook = new();
            using InMemoryConversationSessions sessions = Sessions(hook, TimeProvider.System, InMemoryConversationSessions.DefaultIdleTimeout);

            ConversationSession session = await sessions.GetOrOpenAsync("main", "c1", state: null, Ct);
            _ = await session.RunTurnAsync("hi", Ct);
            await sessions.CloseAsync("main", "c1", Ct);
            await session.FlushNoticesAsync();

            Assert.Equal(UnloadCause.Closed, Assert.Single(hook.Of<ConversationUnloaded>()).Cause);
        }

        [Fact(Timeout = 30_000)]
        public async Task AnIdleSessionUnloadsAsIdle()
        {
            FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
            RecordingHook hook = new();
            using InMemoryConversationSessions sessions = Sessions(hook, time, TimeSpan.FromMinutes(1));

            ConversationSession session = await sessions.GetOrOpenAsync("main", "c1", state: null, Ct);
            _ = await session.RunTurnAsync("hi", Ct);
            await time.WaitForTimersAsync(time.GetUtcNow() + TimeSpan.FromMinutes(1), 1);
            time.Advance(TimeSpan.FromMinutes(1));

            ConversationUnloaded unloaded = await hook.WaitForAsync<ConversationUnloaded>().WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.Equal(UnloadCause.Idle, unloaded.Cause);
        }

        // One session's notices are numbered 1..N with no gap. The idle unload here falls on the same
        // tick as the reader's idle check, which evicts a mailbox no session pins any more.
        [Fact(Timeout = 30_000)]
        public async Task AnIdleUnloadOnTheReadersIdleTickStillContinuesTheSequence()
        {
            FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
            RecordingHook hook = new();
            using InMemoryConversationSessions sessions = Sessions(hook, time, NoticeHub.IdleAfter, hookClock: time);

            ConversationSession session = await sessions.GetOrOpenAsync("main", "c1", state: null, Ct);
            _ = await session.RunTurnAsync("hi", Ct);
            await session.FlushNoticesAsync();
            await time.WaitForTimersAsync(time.GetUtcNow() + NoticeHub.IdleAfter, 2);
            time.Advance(NoticeHub.IdleAfter);

            ConversationUnloaded unloaded = await hook.WaitForAsync<ConversationUnloaded>().WaitAsync(TimeSpan.FromSeconds(10), Ct);
            IReadOnlyList<HookNotice> notices = hook.Notices;
            Assert.Equal(Enumerable.Range(1, notices.Count).Select(i => (long)i), notices.Select(n => n.Scope.Sequence));
            Assert.Equal(hook.Of<ConversationStarted>().Single().Scope.SessionId, unloaded.Scope.SessionId);
        }

        // A host that disposes a session its owner holds does not unpin the mailbox before the unload,
        // so the owner's unload still continues the session's 1..N.
        [Fact(Timeout = 30_000)]
        public async Task AnUnloadAfterTheHostDisposedTheSessionStillContinuesTheSequence()
        {
            FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
            RecordingHook hook = new();
            using InMemoryConversationSessions sessions = Sessions(hook, time, InMemoryConversationSessions.DefaultIdleTimeout, hookClock: time);

            ConversationSession session = await sessions.GetOrOpenAsync("main", "c1", state: null, Ct);
            _ = await session.RunTurnAsync("hi", Ct);
            await session.FlushNoticesAsync();
            await session.DisposeAsync();
            await time.WaitForTimersAsync(time.GetUtcNow() + NoticeHub.IdleAfter, 1);
            time.Advance(NoticeHub.IdleAfter);

            // The reader arms its next idle wait only once it handled this tick, evicting the mailbox or not.
            await time.WaitForTimersAsync(time.GetUtcNow() + NoticeHub.IdleAfter, 1).WaitAsync(TimeSpan.FromSeconds(10), Ct);
            await sessions.CloseAsync("main", "c1", Ct);

            ConversationUnloaded unloaded = await hook.WaitForAsync<ConversationUnloaded>().WaitAsync(TimeSpan.FromSeconds(10), Ct);
            IReadOnlyList<HookNotice> notices = hook.Notices;
            Assert.Equal(Enumerable.Range(1, notices.Count).Select(i => (long)i), notices.Select(n => n.Scope.Sequence));
            Assert.Equal(UnloadCause.Closed, unloaded.Cause);
        }

        // An unload drops nothing, so a dispose step that throws still unloads.
        [Fact(Timeout = 30_000)]
        public async Task AnUnloadWhoseDisposeThrowsIsStillRaised()
        {
            RecordingHook hook = new();
            using DeafChatClient child = new();
            ToolCallingChatClient parent = new(
                "started",
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["agentName"] = "blocker", ["input"] = "work", ["description"] = "d" });
            RoutingChatClientFactory clients = new(parent);
            _ = clients.Route("parent", parent);
            _ = clients.Route("blocker", child);
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                ConfigurationLoader.LoadYaml(HookSessions.BackgroundYaml),
                new AgentCompilationContext(clients) { Hooks = [hook] })["main"];
            Dictionary<string, IConversationSessionFactory> factories = new(StringComparer.Ordinal)
            {
                ["main"] = new ConversationSessionFactory(
                    compiled, new GuardEvaluator(compiled.Configuration.Guards), logger: new WarningThrowingLogger()),
            };
            using InMemoryConversationSessions sessions = new(factories, InMemoryConversationSessions.DefaultIdleTimeout, TimeProvider.System);
            ConversationSession session = await sessions.GetOrOpenAsync("main", "c1", state: null, Ct);
            _ = await session.RunTurnAsync("go", Ct);
            await child.Started.WaitAsync(TimeSpan.FromSeconds(10), Ct);

            _ = await Assert.ThrowsAsync<InvalidOperationException>(async () => await sessions.CloseAsync("main", "c1", Ct));
            child.Release();
            await session.FlushNoticesAsync();

            Assert.Equal(UnloadCause.Closed, Assert.Single(hook.Of<ConversationUnloaded>()).Cause);
        }

        // An end still waiting on a turn the unload outlived goes out before the unload, never after it.
        [Fact(Timeout = 30_000)]
        public async Task AnEndWaitingOnTheTurnIsRaisedBeforeTheUnload()
        {
            RecordingHook hook = new();
            using InMemoryConversationSessions sessions = Sessions(hook, TimeProvider.System, InMemoryConversationSessions.DefaultIdleTimeout);
            ConversationSession session = await sessions.GetOrOpenAsync("main", "c1", state: null, Ct);
            TurnRun unread = await session.StartTurnAsync(new ChatMessage(ChatRole.User, "hi"), origin: null, Ct);
            _ = session.EndConversation(ConversationEndReason.CallerHungUp);

            Task closing = sessions.CloseAsync("main", "c1", Ct).AsTask();
            await unread.DisposeAsync();
            await closing;
            await session.FlushNoticesAsync();

            Assert.Equal(
                [typeof(ConversationEnded), typeof(ConversationUnloaded)],
                hook.Notices.Where(n => n is ConversationEnded or ConversationUnloaded).Select(n => n.GetType()));
        }

        private static InMemoryConversationSessions Sessions(RecordingHook hook, TimeProvider time, TimeSpan idle, TimeProvider? hookClock = null)
        {
            ScriptedChatClient reply = new("hello");
            CompiledAgent compiled = HookSessions.Compile(HookSessions.OneAgentYaml, reply, [hook], time: hookClock)["main"];
            Dictionary<string, IConversationSessionFactory> factories = new(StringComparer.Ordinal)
            {
                ["main"] = new ConversationSessionFactory(compiled, new GuardEvaluator(compiled.Configuration.Guards), timeProvider: time),
            };

            return new InMemoryConversationSessions(factories, idle, time);
        }
    }
}
