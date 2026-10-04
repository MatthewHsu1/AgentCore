using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Gates;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Runtime;
using AgentCore.Application.Tests.Transcript;
using AgentCore.TestSupport;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Hooks
{
    public sealed class SessionHooksTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // ConversationStarted is raised after the store opens, and says how the session found it.
        [Fact]
        public async Task ANewConversationStartsAsNewOnceItsStoreOpens()
        {
            RecordingHook hook = new();
            using ScriptedChatClient reply = new("hello");
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, reply, [hook]);

            await session.FlushNoticesAsync();
            Assert.Empty(hook.Notices);

            _ = await session.RunTurnAsync("hi", Ct);
            await session.FlushNoticesAsync();

            ConversationStarted started = Assert.Single(hook.Of<ConversationStarted>());
            Assert.Equal(ConversationOrigin.New, started.Origin);
            Assert.Equal(session.ConversationId, started.Scope.ConversationId);
            Assert.Equal("main", started.Scope.Entry);
            Assert.Null(started.Scope.TurnIndex);
            Assert.Equal(1, started.Scope.Sequence);
            Assert.NotEqual(Guid.Empty, started.Scope.SessionId);
            Assert.NotEqual(Guid.Empty, started.EventId);
        }

        [Fact]
        public async Task AConversationTheStoreHoldsStartsAsResumed()
        {
            RecordingConversationStore store = new();
            string id = await ConversationSessionResumeTestSupport.FirstTurnAsync(store, "hi", "hello");
            RecordingHook hook = new();
            using ScriptedChatClient reply = new("again");
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, reply, [hook], store, conversationId: id);

            _ = await session.RunTurnAsync("and now?", Ct);
            await session.FlushNoticesAsync();

            Assert.Equal(ConversationOrigin.Resumed, Assert.Single(hook.Of<ConversationStarted>()).Origin);
        }

        [Fact]
        public async Task ASecondSessionOfAConversationThisProcessHeldStartsAsReloaded()
        {
            RecordingHook hook = new();
            RecordingConversationStore store = new();
            using ScriptedChatClient reply = new("hello");
            CompiledAgent compiled = HookSessions.Compile(HookSessions.OneAgentYaml, reply, [hook], store)["main"];
            ConversationSessionFactory factory = new(compiled, new GuardEvaluator(compiled.Configuration.Guards));

            ConversationSession first = factory.Create("c1");
            _ = await first.RunTurnAsync("hi", Ct);
            await first.FlushTranscriptAsync();
            await first.DisposeAsync();
            ConversationSession second = factory.Create("c1");
            _ = await second.RunTurnAsync("again", Ct);
            await second.FlushNoticesAsync();

            Assert.Equal(
                [ConversationOrigin.New, ConversationOrigin.Reloaded],
                hook.Of<ConversationStarted>().Select(started => started.Origin));
        }

        // Two entries of one compile share one runtime, so one conversation id has one Sequence.
        [Fact]
        public async Task TwoFactoriesOfOneCompileShareOneSequence()
        {
            RecordingHook hook = new();
            RecordingConversationStore store = new();
            using ScriptedChatClient reply = new("hello");
            IReadOnlyDictionary<string, CompiledAgent> compiled = HookSessions.Compile(HookSessions.TwoEntryYaml, reply, [hook], store);
            ConversationSessionFactory main = new(compiled["main"], new GuardEvaluator(compiled["main"].Configuration.Guards));
            ConversationSessionFactory other = new(compiled["other"], new GuardEvaluator(compiled["other"].Configuration.Guards));

            ConversationSession a = main.Create("shared");
            _ = await a.RunTurnAsync("hi", Ct);
            await a.FlushTranscriptAsync();
            ConversationSession b = other.Create("shared");
            _ = await b.RunTurnAsync("hi again", Ct);
            await b.FlushNoticesAsync();

            IReadOnlyList<HookNotice> all = hook.Notices;
            Assert.Equal(Enumerable.Range(1, all.Count).Select(static n => (long)n), all.Select(static notice => notice.Scope.Sequence));
            Assert.Equal(["main", "other"], hook.Of<ConversationStarted>().Select(static started => started.Scope.Entry));
            long lastOfA = all.Where(notice => notice.Scope.SessionId == a.Hooks.SessionId).Max(static notice => notice.Scope.Sequence);
            ConversationStarted startedB = Assert.Single(hook.Of<ConversationStarted>(), started => started.Scope.SessionId == b.Hooks.SessionId);
            Assert.True(startedB.Scope.Sequence > lastOfA);
        }

        // A factory's own hooks get notices; a gate hook must be compiled in.
        [Fact]
        public async Task AFactoryHookHearsTheSessionsItBuilds()
        {
            RecordingHook hook = new();
            using ScriptedChatClient reply = new("hello");
            CompiledAgent compiled = HookSessions.Compile(HookSessions.OneAgentYaml, reply)["main"];
            ConversationSession session = new ConversationSessionFactory(
                compiled, new GuardEvaluator(compiled.Configuration.Guards), hooks: [hook]).Create();

            _ = await session.RunTurnAsync("hi", Ct);
            await session.FlushNoticesAsync();

            _ = Assert.Single(hook.Of<ConversationStarted>());
        }

        [Fact]
        public void AFactoryHookThatOverridesAGateIsRefused()
        {
            using ScriptedChatClient reply = new("hello");
            CompiledAgent compiled = HookSessions.Compile(HookSessions.OneAgentYaml, reply)["main"];

            ArgumentException refused = Assert.Throws<ArgumentException>(() => new ConversationSessionFactory(
                compiled, new GuardEvaluator(compiled.Configuration.Guards), hooks: [new ToolGateHook()]));

            Assert.Contains(nameof(AgentCompilationContext.Hooks), refused.Message, StringComparison.Ordinal);
            Assert.Equal("hooks", refused.ParamName);
        }

        // A loaded session pins its mailbox, so an idle eviction never restarts its Sequence.
        [Fact(Timeout = 10_000)]
        public async Task ALoadedSessionKeepsItsSequenceAcrossTheIdleEviction()
        {
            FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
            RecordingHook hook = new();
            using ScriptedChatClient reply = new("hello");
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, reply, [hook], time: time);

            _ = await session.RunTurnAsync("hi", Ct);
            await session.FlushNoticesAsync();
            await time.WaitForTimersAsync(time.GetUtcNow() + NoticeHub.IdleAfter, 1);
            time.Advance(NoticeHub.IdleAfter);
            await time.WaitForTimersAsync(time.GetUtcNow() + NoticeHub.IdleAfter, 1);
            _ = session.Hooks.Raise(new TurnStarted(session.Hooks.Scope(turnIndex: 0, stage: string.Empty), "only", "hi"));
            await session.FlushNoticesAsync();

            IReadOnlyList<HookNotice> all = hook.Notices;
            Assert.Equal(Enumerable.Range(1, all.Count).Select(static n => (long)n), all.Select(static notice => notice.Scope.Sequence));
            Assert.Contains(all, static notice => notice is TurnStarted);
        }

        [Fact(Timeout = 10_000)]
        public async Task ADisposedSessionLetsItsMailboxGoOnceIdle()
        {
            FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
            using ScriptedChatClient reply = new("hello");
            CompiledAgent compiled = HookSessions.Compile(HookSessions.OneAgentYaml, reply, [new RecordingHook()], time: time)["main"];
            ConversationSession session = new ConversationSessionFactory(compiled, new GuardEvaluator(compiled.Configuration.Guards), timeProvider: time).Create("c1");
            _ = await session.RunTurnAsync("hi", Ct);
            await session.FlushNoticesAsync();

            await session.DisposeAsync();
            await time.WaitForTimersAsync(time.GetUtcNow() + NoticeHub.IdleAfter, 1);
            time.Advance(NoticeHub.IdleAfter);

            await NoticeProbes.WaitUntilAsync(() => compiled.Hooks.Notices.MailboxCount == 0);
        }

        // A dispose that throws still lets go of the pin. Here the release of a child that ignores its cancel logs a
        // warning after its 5 s limit, and the logger throws.
        [Fact(Timeout = 30_000)]
        public async Task ASessionWhoseDisposeThrowsStillLetsItsMailboxGo()
        {
            FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
            using DeafChatClient child = new();
            ToolCallingChatClient parent = new(
                "started",
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["agentName"] = "blocker", ["input"] = "work", ["description"] = "d" });
            RoutingChatClientFactory clients = new(parent);
            _ = clients.Route("parent", parent);
            _ = clients.Route("blocker", child);
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                ConfigurationLoader.LoadYaml(HookSessions.BackgroundYaml),
                new AgentCompilationContext(clients) { Hooks = [new RecordingHook()], Clock = time })["main"];
            ConversationSession session = new ConversationSessionFactory(
                compiled, new GuardEvaluator(compiled.Configuration.Guards), timeProvider: time, logger: new WarningThrowingLogger()).Create("c1");
            _ = await session.RunTurnAsync("go", Ct);
            await session.FlushNoticesAsync();
            await child.Started.WaitAsync(TimeSpan.FromSeconds(10), Ct);

            _ = await Assert.ThrowsAsync<InvalidOperationException>(async () => await session.DisposeAsync());
            child.Release();
            await time.WaitForTimersAsync(time.GetUtcNow() + NoticeHub.IdleAfter, 1);
            time.Advance(NoticeHub.IdleAfter);

            await NoticeProbes.WaitUntilAsync(() => compiled.Hooks.Notices.MailboxCount == 0);
        }

        // A factory that built a hub for its own hooks drains it when it is disposed.
        [Fact(Timeout = 10_000)]
        public async Task DisposingAFactoryWaitsForTheNoticesItsHooksStillHold()
        {
            HoldingHook hook = new();
            using ScriptedChatClient reply = new("hello");
            CompiledAgent compiled = HookSessions.Compile(HookSessions.OneAgentYaml, reply)["main"];
            ConversationSessionFactory factory = new(compiled, new GuardEvaluator(compiled.Configuration.Guards), hooks: [hook]);
            ConversationSession session = factory.Create();
            _ = await session.RunTurnAsync("hi", Ct);
            await hook.Entered.Task;

            Task disposing = factory.DisposeAsync().AsTask();

            Assert.False(disposing.IsCompleted);
            hook.Release.SetResult();
            await disposing;
            Assert.True(hook.Finished);
        }

        private sealed class HoldingHook : AgentHook
        {
            public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public bool Finished { get; private set; }

            public override TimeSpan? NoticeTimeout => null;

            public override async ValueTask OnConversationStartedAsync(ConversationStarted notice, CancellationToken cancellationToken)
            {
                _ = Entered.TrySetResult();
                await Release.Task.ConfigureAwait(false);
                Finished = true;
            }
        }

        private sealed class ToolGateHook : AgentHook
        {
            public override ValueTask BeforeToolAsync(ToolGate gate, CancellationToken cancellationToken) => default;
        }
    }
}
