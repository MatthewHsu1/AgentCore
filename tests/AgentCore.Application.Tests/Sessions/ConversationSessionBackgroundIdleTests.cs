using System.Text.Json;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Runtime;
using AgentCore.Application.Runtime.Harness;
using AgentCore.Application.Sessions.Memory;
using AgentCore.Application.Tests.Fakes;
using AgentCore.TestSupport;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Xunit;
using static AgentCore.Application.Tests.Sessions.ConversationSessionsFixture;

namespace AgentCore.Application.Tests.Sessions
{
    /// <summary>
    /// Issue #31: a running <c>background:</c> child counts as activity for the held-session idle timer, the
    /// same way a running turn does (<see cref="ConversationSessionIdleTimeoutTests"/>).
    /// </summary>
#pragma warning disable MAAI001 // BackgroundAgentsProvider is evaluation-only in Microsoft.Agents.AI 1.21.0.
    public sealed class ConversationSessionBackgroundIdleTests
    {
        private static readonly TimeSpan IdleTimeout = InMemoryConversationSessions.DefaultIdleTimeout;

        private const string LingeringChildYaml =
            """
          apiVersion: agentcore/v1
          guards:
            never: { "<": [ { var: turnIndex }, 0 ] }
          agents:
            items:
              - { id: parent, instructions: "delegate work", model: { ref: parent }, background: [blocker] }
              - { id: blocker, instructions: "work forever", model: { ref: blocker } }
          entries:
            main:
              policy:
                initial: working
                stages:
                  - { id: working, agent: parent, to: [ { stage: done, when: never } ] }
                  - { id: done, agent: blocker, terminal: true }
          """;

        [Fact]
        public async Task ABackgroundChildStillRunningAtTheIdleTimeoutKeepsTheSessionHeld()
        {
            FakeTimeProvider clock = Clock();
            GatedChatClient child = new(new ScriptedChatClient("done"));
            child.Arm();
            using InMemoryConversationSessions sessions = new(
                SingleEntrySessionFactories.Of(BuildFactory(child)), IdleTimeout, clock);

            ConversationSession session =
                await sessions.GetOrOpenAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", null, Token);
            _ = await session.RunTurnAsync("go", Token);
            await child.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

            // The turn itself is over; only the child it started is still going.
            Assert.Null(session.Cuts.RunningTurn());

            clock.Advance(IdleTimeout);

            Assert.Equal(1, sessions.Count);
            Assert.False(IsDisposed(session));

            _ = child.Open.TrySetResult();
        }

        [Fact]
        public async Task ABackgroundChildStillRunningAtTheIdleTimeoutStampsTheWorkspace()
        {
            // The workspace is stamped wherever the idle poll treats a running child as activity, not only at
            // turn start, so another server's boot sweep does not mistake this folder for dead while a
            // background child, not a turn, is the only thing still using it.
            string root = Directory.CreateTempSubdirectory("agentcore-ws-bg-").FullName;
            try
            {
                FakeTimeProvider clock = Clock();
                GatedChatClient child = new(new ScriptedChatClient("done"));
                child.Arm();
                using InMemoryConversationSessions sessions = new(
                    SingleEntrySessionFactories.Of(BuildFactory(child, workspaceRoot: root, timeProvider: clock)), IdleTimeout, clock);

                ConversationSession session =
                    await sessions.GetOrOpenAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", null, Token);
                _ = await session.RunTurnAsync("go", Token);
                await child.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

                Assert.Null(session.Cuts.RunningTurn());
                DateTime stampAfterTurn = Directory.GetLastWriteTimeUtc(session.Workspace!);

                clock.Advance(IdleTimeout);

                Assert.Equal(clock.GetUtcNow().UtcDateTime, Directory.GetLastWriteTimeUtc(session.Workspace!));
                Assert.NotEqual(stampAfterTurn, Directory.GetLastWriteTimeUtc(session.Workspace!));

                _ = child.Open.TrySetResult();
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task ABackgroundChildStillRunningWellBeforeTheIdleTimeoutStampsTheWorkspaceAtEachPoll()
        {
            // The poll this test checks lands well inside the idle timeout, not at the moment the timeout is
            // crossed: the whole background-poll window stamps the workspace, not only its last tick.
            string root = Directory.CreateTempSubdirectory("agentcore-ws-bg-early-").FullName;
            try
            {
                FakeTimeProvider clock = Clock();
                GatedChatClient child = new(new ScriptedChatClient("done"));
                child.Arm();
                using InMemoryConversationSessions sessions = new(
                    SingleEntrySessionFactories.Of(BuildFactory(child, workspaceRoot: root, timeProvider: clock)), IdleTimeout, clock);

                ConversationSession session =
                    await sessions.GetOrOpenAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", null, Token);
                _ = await session.RunTurnAsync("go", Token);
                await child.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

                DateTime stampAfterTurn = Directory.GetLastWriteTimeUtc(session.Workspace!);

                clock.Advance(HeldSession.BackgroundChildPollInterval);

                Assert.Equal(clock.GetUtcNow().UtcDateTime, Directory.GetLastWriteTimeUtc(session.Workspace!));
                Assert.NotEqual(stampAfterTurn, Directory.GetLastWriteTimeUtc(session.Workspace!));

                _ = child.Open.TrySetResult();
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task TheSessionUnloadsWithinOnePollAfterTheIdleTimeoutOnceTheChildEnds()
        {
            FakeTimeProvider clock = Clock();
            GatedChatClient child = new(new ScriptedChatClient("done"));
            child.Arm();
            using InMemoryConversationSessions sessions = new(
                SingleEntrySessionFactories.Of(BuildFactory(child)), IdleTimeout, clock);

            ConversationSession session =
                await sessions.GetOrOpenAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", null, Token);
            _ = await session.RunTurnAsync("go", Token);
            await child.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

            clock.Advance(IdleTimeout);
            Assert.Equal(1, sessions.Count);

            // The child is read as running right up to this check, so this is where the fresh idle window
            // starts from, not the later poll that first notices it is gone.
            _ = child.Open.TrySetResult();

            // The provider's own bookkeeping settles on a real thread; give it real time to catch up before
            // the fake clock drives the next poll, or the poll below could still see the child as running.
            await EventuallyAsync(() => !session.Lifetime.HasRunningBackgroundChild());

            // Comfortably inside the fresh idle timeout: held.
            clock.Advance(IdleTimeout - HeldSession.BackgroundChildPollInterval);
            Assert.Equal(1, sessions.Count);
            Assert.False(IsDisposed(session));

            // Comfortably past idleTimeout + one poll interval since that point: unloaded, within the bound
            // BackgroundChildPollInterval's doc comment states.
            clock.Advance(TimeSpan.FromMinutes(1) + HeldSession.BackgroundChildPollInterval);
            Assert.Equal(0, sessions.Count);
            await EventuallyAsync(() => IsDisposed(session));
        }

        [Fact]
        public async Task AChildThatEndsWellBeforeTheIdleTimeoutStillCountsAsActivity()
        {
            // Issue #31 fix round, defect 2: the child is released long before the idle timeout comes due at
            // all. A check only at the deadline would find it already gone and expire right there, on schedule,
            // as if the child had never run. The document is polled through the whole window instead, so the
            // child's end (not the original deadline) is what the idle timeout counts from.
            FakeTimeProvider clock = Clock();
            GatedChatClient child = new(new ScriptedChatClient("done"));
            child.Arm();
            using InMemoryConversationSessions sessions = new(
                SingleEntrySessionFactories.Of(BuildFactory(child)), IdleTimeout, clock);

            ConversationSession session =
                await sessions.GetOrOpenAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", null, Token);

            // An explicit touch before anything else happens. It must re-arm at the shorter background poll
            // cadence, not at a plain idle timeout, or the poll below would never fire and this test would pass
            // for the wrong reason.
            Assert.NotNull(await sessions.TryGetAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", Token));

            _ = await session.RunTurnAsync("go", Token);
            await child.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

            TimeSpan childReleasedAt = TimeSpan.FromMinutes(2);
            clock.Advance(childReleasedAt);

            _ = child.Open.TrySetResult();
            await EventuallyAsync(() => !session.Lifetime.HasRunningBackgroundChild());

            // One poll tick notices the child is gone. Fake time is now childReleasedAt + one poll interval.
            clock.Advance(HeldSession.BackgroundChildPollInterval);

            // The moment the original idle timeout, counted from session open rather than from the child's end,
            // would have expired the session under the old bug. It must still be held.
            TimeSpan sinceOpen = childReleasedAt + HeldSession.BackgroundChildPollInterval;
            clock.Advance(IdleTimeout - sinceOpen);
            Assert.Equal(1, sessions.Count);
            Assert.False(IsDisposed(session));

            // It does unload once its own idle timeout, counted from the child's end, is comfortably up.
            clock.Advance(IdleTimeout);
            Assert.Equal(0, sessions.Count);
            await EventuallyAsync(() => IsDisposed(session));
        }

        [Fact]
        public async Task TheBackgroundPollIntervalIsClampedToTheIdleTimeout()
        {
            // Issue #31 fix round, defect 4: an idle timeout shorter than the poll interval must not let the
            // session outlive its own timeout just because nothing polled it in time.
            FakeTimeProvider clock = Clock();
            TimeSpan shortIdleTimeout = TimeSpan.FromSeconds(5);
            Assert.True(shortIdleTimeout < HeldSession.BackgroundChildPollInterval);

            GatedChatClient child = new(new ScriptedChatClient("done"));
            using InMemoryConversationSessions sessions = new(
                SingleEntrySessionFactories.Of(BuildFactory(child)), shortIdleTimeout, clock);

            _ = await sessions.GetOrOpenAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", null, Token);

            clock.Advance(shortIdleTimeout);

            Assert.Equal(0, sessions.Count);
        }

        [Fact]
        public async Task AFaultReadingWhetherAChildIsRunningIsTreatedAsOneStillRunningAndLogged()
        {
            // Issue #31 fix round, defect 1: GetIncompleteTasks can throw. A state bag entry set through a live
            // SetValue<T> call always short-circuits to a CLR type check (proven while chasing this down: a
            // mismatched cached type returns false, never touching JSON), so the only path that reaches a real
            // JsonException is a session resumed from stored JSON, where a key comes back shaped for a
            // different provider version. Thrown on the idle timer thread with no catch, this either crashes the
            // process (TimeProvider.System) or leaves the timer never re-armed (a fake clock): reviewer-proven.
            RecordingLoggerFactory logs = new();
            ConversationSessionFactory factory =
                BuildFactory(new GatedChatClient(new ScriptedChatClient("done")), logs.CreateLogger("session"));
            await using ConversationSession session = factory.Create("conversation-1");

            BackgroundAgentsProvider provider = Assert.Single(session.Compiled.BackgroundProviders);

            Dictionary<string, JsonElement> poisoned = new(StringComparer.Ordinal);
            foreach (string key in provider.StateKeys)
            {
                poisoned[key] = JsonSerializer.SerializeToElement("not the object the provider expects");
            }

            session.AgentSession = await session.Compiled.TurnAgent.DeserializeSessionAsync(
                HarnessSessionState.Wrap(poisoned), cancellationToken: Token);

            bool result = session.Lifetime.HasRunningBackgroundChild();

            Assert.True(result);
            CapturedLine line = Assert.Single(logs.Of(45));
            Assert.Equal(LogLevel.Warning, line.Level);
            Assert.NotNull(line.Exception);
        }

        private static ConversationSessionFactory BuildFactory(
            IChatClient child, ILogger? logger = null, string? workspaceRoot = null, TimeProvider? timeProvider = null)
        {
            ToolCallingChatClient parent = new(
                "started",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["agentName"] = "blocker",
                    ["input"] = "work",
                    ["description"] = "d",
                });

            RoutingChatClientFactory chatClients = new(parent);
            _ = chatClients.Route("parent", parent);
            _ = chatClients.Route("blocker", child);

            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                ConfigurationLoader.LoadYaml(LingeringChildYaml),
                new AgentCompilationContext(chatClients) { WorkspaceRoot = workspaceRoot })[SingleEntrySessionFactories.MainEntry];

            return new ConversationSessionFactory(
                compiled,
                new GuardEvaluator(compiled.Configuration.Guards),
                logger: logger,
                timeProvider: timeProvider,
                workspaceRoot: workspaceRoot);
        }

        /// <summary>Reads whether the session was disposed: a disposed session refuses the turn slot.</summary>
        private static bool IsDisposed(ConversationSession session)
        {
            try
            {
                if (session.Cuts.TryEnterTurn())
                {
                    session.Cuts.ReleaseTurn();
                }

                return false;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        }
    }
#pragma warning restore MAAI001
}
