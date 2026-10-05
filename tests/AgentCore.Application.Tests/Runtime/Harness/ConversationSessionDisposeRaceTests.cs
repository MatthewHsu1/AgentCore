using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Domain;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Runtime.Harness
{
    /// <summary>
    /// A conversation disposed while a turn is on its way in: the dispose refuses a turn not yet admitted, and waits for
    /// an admitted one to end before it releases, so no child of that turn outlives the conversation.
    /// </summary>
    public sealed class ConversationSessionDisposeRaceTests
    {
        private const string LingeringChildYaml =
            """
          apiVersion: agentcore/v1
          agents:
            items:
              - { id: parent, instructions: "delegate work", model: { ref: parent }, background: [blocker] }
              - { id: blocker, instructions: "work forever", model: { ref: blocker } }
          entries:
            main:
              agent: parent
          """;

        private static CancellationToken Token => TestContext.Current.CancellationToken;

        [Fact]
        public async Task DisposeWhileATurnReadsItsCatchUp_RefusesTheTurn_AndItRunsNothing()
        {
            GatedReadConversationStore store = new();
            using HangUntilCancelledChatClient child = new();
            ToolCallingChatClient parent = new("started", StartArgs());
            ConversationSession a = Build(parent, child, store).Create("conversation-1");
            await using ConversationSession b = Build(new ScriptedChatClient("fine"), new HangUntilCancelledChatClient(), store).Create("conversation-1");

            _ = await a.RunTurnAsync("go", Token);
            await child.Started.WaitAsync(TimeSpan.FromSeconds(10), Token);
            _ = await b.RunTurnAsync("hello from the other host", Token);
            int calls = parent.Calls;

            store.Arm();
            Task<TurnResult> turn = a.RunTurnAsync("status?", Token);
            await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

            await a.DisposeAsync();
            store.Open.SetResult();

            _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => turn);
            Assert.True(child.Cancelled.IsCompleted);
            Assert.Equal(calls, parent.Calls);
        }

        [Fact]
        public async Task DisposeWhileATurnRuns_WaitsForTheTurn_ThenCancelsTheChildItStarted()
        {
            InMemoryConversationStore store = new();
            using HangUntilCancelledChatClient child = new();
            GatedChatClient parent = new(new AfterTaskChatClient(new ToolCallingChatClient("started", StartArgs()), child.Started));
            ConversationSession a = Build(parent, child, store).Create("conversation-1");

            parent.Arm();
            Task<TurnResult> turn = a.RunTurnAsync("go", Token);
            await parent.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

            Task disposed = a.DisposeAsync().AsTask();
            parent.Open.SetResult();

            Assert.Equal("started", (await turn).ReplyText);
            await disposed.WaitAsync(TimeSpan.FromSeconds(10), Token);
            await child.Cancelled.WaitAsync(TimeSpan.FromSeconds(10), Token);
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

        private static ConversationSessionFactory Build(IChatClient parent, IChatClient child, IConversationStore store)
        {
            RoutingChatClientFactory chatClients = new(parent);
            _ = chatClients.Route("parent", parent);
            _ = chatClients.Route("blocker", child);

            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                ConfigurationLoader.LoadYaml(LingeringChildYaml), new AgentCompilationContext(chatClients) { ConversationStore = store })["main"];

            return new ConversationSessionFactory(compiled, new GuardEvaluator(compiled.Configuration.Guards));
        }
    }
}
