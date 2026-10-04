using System.Diagnostics;
using System.Text.Json;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Domain;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Runtime.Harness
{
    /// <summary>
    /// A session that catches up on another host's turn swaps its MAF session for one read from the store, and
    /// releases the old one. The turn does not wait for that release, and no snapshot sees half of the swap.
    /// </summary>
    public sealed class ConversationSessionCatchUpReleaseTests
    {
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

        private const string LingeringTodosYaml =
            """
          apiVersion: agentcore/v1
          agents:
            items:
              - { id: parent, instructions: "delegate work", todos: true, model: { ref: parent }, background: [blocker] }
              - { id: blocker, instructions: "work forever", model: { ref: blocker } }
          entries:
            main:
              agent: parent
          """;

        private const int BackgroundReleaseTimedOut = 43;

        private static CancellationToken Token => TestContext.Current.CancellationToken;

        [Fact]
        public async Task ACatchUp_WithAChildThatIgnoresItsCancel_DoesNotHoldTheTurnForTheRelease()
        {
            InMemoryConversationStore store = new();
            using DeafChatClient child = new();
            await using ConversationSession a = Build(new ToolCallingChatClient("started", StartArgs()), child, store).Create("conversation-1");
            await using ConversationSession b = Build(new ScriptedChatClient("fine"), new DeafChatClient(), store).Create("conversation-1");

            await StartAChildThenLetTheOtherHostTakeATurnAsync(a, b, child);

            // The release gives a deaf child 5 s before it gives up, so a turn that waited for it takes 5 s or more.
            Stopwatch clock = Stopwatch.StartNew();
            TurnResult caughtUp = await a.RunTurnAsync("status?", Token);
            clock.Stop();

            Assert.Equal(2, caughtUp.TurnIndex);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4), $"the catch-up turn took {clock.Elapsed.TotalMilliseconds:F0} ms");

            child.Release();
        }

        [Fact]
        public async Task DisposeAsync_WaitsForTheReleaseOfTheSessionACatchUpReplaced()
        {
            RecordingLoggerFactory logs = new();
            InMemoryConversationStore store = new();
            using DeafChatClient child = new();
            ConversationSession a = Build(new ToolCallingChatClient("started", StartArgs()), child, store, logs.CreateLogger("a")).Create("conversation-1");
            await using ConversationSession b = Build(new ScriptedChatClient("fine"), new DeafChatClient(), store).Create("conversation-1");

            await StartAChildThenLetTheOtherHostTakeATurnAsync(a, b, child);
            _ = await a.RunTurnAsync("status?", Token);

            await a.DisposeAsync();

            CapturedLine timedOut = Assert.Single(logs.Of(BackgroundReleaseTimedOut));
            Assert.Equal(LogLevel.Warning, timedOut.Level);
            Assert.Equal("conversation-1", timedOut.Field<string>("ConversationId"));
        }

        [Fact]
        public async Task ASnapshotDuringACatchUp_NeverPairsTheOtherHostsProvidersWithTheOldTurnIndex()
        {
            InMemoryConversationStore store = new();
            using DeafChatClient child = new();
            GatedChatClient parent = new(
                new ScriptedToolCallingChatClient(("background_agents_start_task", /*lang=json,strict*/ """{"agentName":"blocker","input":"work","description":"d"}"""))
                {
                    FinalText = "started",
                });
            ScriptedToolCallingChatClient other = new(("todos_add", /*lang=json,strict*/ """{"todos":[{"title":"from the other host"}]}"""))
            {
                FinalText = "fine",
            };
            await using ConversationSession a = Build(parent, child, store, yaml: LingeringTodosYaml).Create("conversation-1");
            await using ConversationSession b = Build(other, new DeafChatClient(), store, yaml: LingeringTodosYaml).Create("conversation-1");

            await StartAChildThenLetTheOtherHostTakeATurnAsync(a, b, child);
            string own = Providers(a.States.Snapshot());
            ConversationRecord? record = await store.GetAsync("conversation-1", Token);
            Assert.Equal(2, record!.State!.NextTurnIndex);
            Assert.NotEqual(own, Providers(record.State));

            parent.Arm();
            Task<TurnResult> caughtUp = a.RunTurnAsync("status?", Token);

            // Snapshots taken from the turn's start until its model call: every one of them is either wholly before
            // the catch-up or wholly after it.
            List<ConversationSessionState> seen = [];
            while (!parent.Entered.Task.IsCompleted && !caughtUp.IsCompleted)
            {
                seen.Add(a.States.Snapshot());
                await Task.Delay(TimeSpan.FromMilliseconds(1), Token);
            }

            seen.Add(a.States.Snapshot());

            Assert.All(seen, snapshot => Assert.Equal(snapshot.NextTurnIndex == 1, Providers(snapshot) == own));
            Assert.Equal(2, seen[^1].NextTurnIndex);

            parent.Open.SetResult();
            Assert.Equal(2, (await caughtUp).TurnIndex);
            child.Release();
        }

        private static async Task StartAChildThenLetTheOtherHostTakeATurnAsync(ConversationSession a, ConversationSession b, DeafChatClient child)
        {
            _ = await a.RunTurnAsync("go", Token);
            await child.Started.WaitAsync(TimeSpan.FromSeconds(10), Token);
            _ = await b.RunTurnAsync("hello from the other host", Token);
        }

        private static string Providers(ConversationSessionState state)
        {
            return JsonSerializer.Serialize(state.Providers);
        }

        private static Dictionary<string, object?> StartArgs()
        {
            return new(StringComparer.Ordinal)
            {
                ["agentName"] = "blocker",
                ["input"] = "work",
                ["description"] = "d",
            };
        }

        private static ConversationSessionFactory Build(
            IChatClient parent, IChatClient child, IConversationStore store, ILogger? logger = null, string yaml = LingeringChildYaml)
        {
            RoutingChatClientFactory chatClients = new(parent);
            _ = chatClients.Route("parent", parent);
            _ = chatClients.Route("blocker", child);

            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                ConfigurationLoader.LoadYaml(yaml), new AgentCompilationContext(chatClients) { ConversationStore = store })["main"];

            return new ConversationSessionFactory(compiled, new GuardEvaluator(compiled.Configuration.Guards), logger: logger);
        }
    }
}
