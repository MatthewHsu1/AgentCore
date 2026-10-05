using System.Text.Json;
using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime.Turn;
using AgentCore.TestSupport;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Domain;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Runtime.Harness
{
    /// <summary>
    /// A graph row whose document declares a harness switch keeps one workflow
    /// session for the whole conversation, so <c>todos:</c> (and <c>mode:</c>) survive turns and resume.
    /// A graph row without one still runs every turn fresh.
    /// </summary>
    public sealed class ConversationSessionGraphStateTests
    {
        private const string TodosYaml =
            """
          apiVersion: agentcore/v1
          agents:
            items:
              - { id: adder, instructions: "track todos", todos: true, model: { ref: adder } }
              - { id: echo, instructions: "echo back", model: { ref: echo } }
          entries:
            main:
              graph:
                pattern: sequential
                agents: [ adder, echo ]
          """;

        private const string PlainYaml =
            """
          apiVersion: agentcore/v1
          agents:
            items:
              - { id: first, instructions: "one", model: { ref: first } }
              - { id: second, instructions: "two", model: { ref: second } }
          entries:
            main:
              graph:
                pattern: sequential
                agents: [ first, second ]
          """;

        [Fact]
        public async Task SecondTurn_AdderRequestCarriesTheRestoredTodo_AndNoRenderedHistory()
        {
            InMemoryConversationStore store = new();
            ScriptedToolCallingChatClient adder = new(("todos_add", /*lang=json,strict*/ """{"todos":[{"title":"buy milk"}]}""")) { FinalText = "added" };
            RequestCapturingChatClient adderRequests = new(adder);
            ScriptedToolCallingChatClient echo = new() { FinalText = "echo1" };
            ConversationSessionFactory factory = Build(TodosYaml, store, adderRequests, echo);
            ConversationSession session = factory.Create("conversation-1");

            _ = await session.RunTurnAsync("track this", TestContext.Current.CancellationToken);
            int turn1Conversations = adderRequests.Requests.Count;

            adder.FinalText = "second";
            echo.FinalText = "echo2";
            _ = await session.RunTurnAsync("anything new?", TestContext.Current.CancellationToken);

            // The restored session injects the todo list into the run-2 instructions…
            IReadOnlyList<ChatMessage> run2 = adderRequests.Requests[turn1Conversations];
            Assert.Contains(run2, message => message.Text.Contains("buy milk", StringComparison.Ordinal));

            // …and the resumed conversation already carries what came before, so the turn
            // renders no history of its own. Rendered plus resumed would deliver it twice.
            Assert.DoesNotContain(run2, message => message.Text.Contains(TurnMessages.HistoryPreamble, StringComparison.Ordinal));
        }

        [Fact]
        public async Task ResumeFromTheSameStore_AdderRequestCarriesTheRestoredTodo()
        {
            InMemoryConversationStore store = new();
            ScriptedToolCallingChatClient adder = new(("todos_add", /*lang=json,strict*/ """{"todos":[{"title":"buy milk"}]}""")) { FinalText = "added" };
            RequestCapturingChatClient adderRequests = new(adder);
            ScriptedToolCallingChatClient echo = new() { FinalText = "echo1" };
            ConversationSessionFactory factory = Build(TodosYaml, store, adderRequests, echo);

            ConversationSession first = factory.Create("conversation-1");
            _ = await first.RunTurnAsync("track this", TestContext.Current.CancellationToken);

            ConversationSession second = factory.Create("conversation-1");
            _ = await second.RunTurnAsync("anything new?", TestContext.Current.CancellationToken);

            IReadOnlyList<ChatMessage> resumed = adderRequests.Requests[^1];
            Assert.Contains(resumed, message => message.Text.Contains("buy milk", StringComparison.Ordinal));
        }

        [Fact]
        public async Task ASessionThatCaughtUpOnAnotherSessionsTurn_ResumesTheWorkflowThatTurnStored_AndKeepsIt()
        {
            InMemoryConversationStore store = new();
            RequestCapturingChatClient adder = new(new TodoFromCallerChatClient());
            ConversationSessionFactory factory = Build(TodosYaml, store, adder, new ScriptedToolCallingChatClient() { FinalText = "echo" });
            ConversationSession a = factory.Create("conversation-1");
            ConversationSession b = factory.Create("conversation-1");

            _ = await a.RunTurnAsync("add buy milk", TestContext.Current.CancellationToken);
            _ = await b.RunTurnAsync("add call the bank", TestContext.Current.CancellationToken);
            _ = await a.RunTurnAsync("anything new?", TestContext.Current.CancellationToken);
            await a.FlushTranscriptAsync();

            Assert.Contains(adder.Requests[^1], message => message.Text.Contains("call the bank", StringComparison.Ordinal));
            ConversationRecord? record = await store.GetAsync("conversation-1", TestContext.Current.CancellationToken);
            Assert.Equal(3, record?.State?.NextTurnIndex);
            Assert.Contains("call the bank", record!.State!.WorkflowState!.Value.GetRawText(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task NoHarnessSwitch_AfterATurn_Store0HoldsNeitherBlobNorProviders()
        {
            InMemoryConversationStore store = new();
            ConversationSessionFactory factory = Build(
                PlainYaml,
                store,
                new ScriptedToolCallingChatClient() { FinalText = "one" },
                new ScriptedToolCallingChatClient() { FinalText = "two" });
            ConversationSession session = factory.Create("conversation-1");

            TurnResult turn = await session.RunTurnAsync("go", TestContext.Current.CancellationToken);

            Assert.Equal("two", turn.ReplyText);
            ConversationRecord? record = await store.GetAsync("conversation-1", TestContext.Current.CancellationToken);
            Assert.NotNull(record?.State);
            Assert.Null(record.State.WorkflowState);
            Assert.Empty(record.State.Providers);
        }

        [Fact]
        public async Task TodosSwitch_AfterATurn_Store0HoldsTheBlobAndNoPickedKeys()
        {
            InMemoryConversationStore store = new();
            ScriptedToolCallingChatClient adder = new(("todos_add", /*lang=json,strict*/ """{"todos":[{"title":"buy milk"}]}""")) { FinalText = "added" };
            ConversationSessionFactory factory = Build(TodosYaml, store, adder, new ScriptedToolCallingChatClient() { FinalText = "echo1" });
            ConversationSession session = factory.Create("conversation-1");

            _ = await session.RunTurnAsync("track this", TestContext.Current.CancellationToken);

            ConversationRecord? record = await store.GetAsync("conversation-1", TestContext.Current.CancellationToken);
            Assert.NotNull(record?.State);
            _ = Assert.NotNull(record.State.WorkflowState);
            Assert.Empty(record.State.Providers);
        }

        [Fact]
        public async Task PickedProvidersWithoutABlob_AreIgnored_AdderStartsFresh()
        {
            InMemoryConversationStore store = new();
            ScriptedToolCallingChatClient adder = new() { FinalText = "added" };
            RequestCapturingChatClient adderRequests = new(adder);
            ConversationSessionFactory factory = Build(TodosYaml, store, adderRequests, new ScriptedToolCallingChatClient() { FinalText = "echo1" });

            ConversationSessionState fabricated = new()
            {
                Providers = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    [new TodoProvider().StateKeys[0]] = JsonDocument.Parse(
                        """{"items":[{"id":1,"title":"buy milk","isComplete":false}],"nextId":2}""").RootElement,
                },
            };

            ConversationSession session = factory.Create("conversation-1", fabricated);
            _ = await session.RunTurnAsync("anything new?", TestContext.Current.CancellationToken);

            Assert.DoesNotContain(
                adderRequests.Requests[0],
                message => message.Text.Contains("buy milk", StringComparison.Ordinal));
        }

        private static ConversationSessionFactory Build(string yaml, IConversationStore store, IChatClient first, IChatClient second)
        {
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(yaml);
            RoutingChatClientFactory chatClients = new(first);
            _ = chatClients.Route("echo", second);
            _ = chatClients.Route("second", second);
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                document,
                new AgentCompilationContext(chatClients) { ConversationStore = store })["main"];

            return new ConversationSessionFactory(compiled, new GuardEvaluator(compiled.Configuration.Guards));
        }
    }
}
