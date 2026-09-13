using System.Text.Json;
using AgentCore.Application.Calls;
using AgentCore.Application.Calls.Memory;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Runtime.Turn;
using AgentCore.TestSupport;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Runtime.Harness;

/// <summary>
/// Harness step: a graph row whose document declares a harness switch keeps one workflow
/// session for the whole call, so <c>todos:</c> (and <c>mode:</c>) survive turns and resume.
/// A graph row without one still runs every turn fresh, exactly as before.
/// </summary>
public sealed class CallSessionGraphStateTests
{
    private const string TodosYaml =
        """
        apiVersion: agentcore/v1
        name: graph-todos
        agents:
          items:
            - { id: adder, instructions: "track todos", todos: true, model: { ref: adder } }
            - { id: echo, instructions: "echo back", model: { ref: echo } }
        graph:
          pattern: sequential
          agents: [ adder, echo ]
        """;

    private const string PlainYaml =
        """
        apiVersion: agentcore/v1
        name: graph-plain
        agents:
          items:
            - { id: first, instructions: "one", model: { ref: first } }
            - { id: second, instructions: "two", model: { ref: second } }
        graph:
          pattern: sequential
          agents: [ first, second ]
        """;

    [Fact]
    public async Task SecondTurn_AdderRequestCarriesTheRestoredTodo_AndNoRenderedHistory()
    {
        InMemoryCallStore store = new();
        var adder = new ScriptedToolCallingChatClient(("todos_add", """{"todos":[{"title":"buy milk"}]}""")) { FinalText = "added" };
        var adderRequests = new RequestCapturingChatClient(adder);
        var echo = new ScriptedToolCallingChatClient() { FinalText = "echo1" };
        var factory = Build(TodosYaml, store, adderRequests, echo);
        var session = factory.Create("call-1");

        await session.RunTurnAsync("track this", TestContext.Current.CancellationToken);
        var turn1Calls = adderRequests.Requests.Count;

        adder.FinalText = "second";
        echo.FinalText = "echo2";
        await session.RunTurnAsync("anything new?", TestContext.Current.CancellationToken);

        // The restored session injects the todo list into the run-2 instructions…
        var run2 = adderRequests.Requests[turn1Calls];
        Assert.Contains(run2, message => message.Text.Contains("buy milk", StringComparison.Ordinal));

        // …and the resumed conversation already carries what came before, so the turn
        // renders no history of its own. Rendered plus resumed would deliver it twice.
        Assert.DoesNotContain(run2, message => message.Text.Contains(TurnMessages.HistoryPreamble, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResumeFromTheSameStore_AdderRequestCarriesTheRestoredTodo()
    {
        InMemoryCallStore store = new();
        var adder = new ScriptedToolCallingChatClient(("todos_add", """{"todos":[{"title":"buy milk"}]}""")) { FinalText = "added" };
        var adderRequests = new RequestCapturingChatClient(adder);
        var echo = new ScriptedToolCallingChatClient() { FinalText = "echo1" };
        var factory = Build(TodosYaml, store, adderRequests, echo);

        var first = factory.Create("call-1");
        await first.RunTurnAsync("track this", TestContext.Current.CancellationToken);

        var second = factory.Create("call-1");
        await second.RunTurnAsync("anything new?", TestContext.Current.CancellationToken);

        var resumed = adderRequests.Requests[^1];
        Assert.Contains(resumed, message => message.Text.Contains("buy milk", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoHarnessSwitch_AfterATurn_Store0HoldsNeitherBlobNorProviders()
    {
        InMemoryCallStore store = new();
        var factory = Build(
            PlainYaml,
            store,
            new ScriptedToolCallingChatClient() { FinalText = "one" },
            new ScriptedToolCallingChatClient() { FinalText = "two" });
        var session = factory.Create("call-1");

        var turn = await session.RunTurnAsync("go", TestContext.Current.CancellationToken);

        Assert.Equal("two", turn.ReplyText);
        var record = await store.GetAsync("call-1", TestContext.Current.CancellationToken);
        Assert.NotNull(record?.State);
        Assert.Null(record.State.WorkflowState);
        Assert.Empty(record.State.Providers);
    }

    [Fact]
    public async Task TodosSwitch_AfterATurn_Store0HoldsTheBlobAndNoPickedKeys()
    {
        InMemoryCallStore store = new();
        var adder = new ScriptedToolCallingChatClient(("todos_add", """{"todos":[{"title":"buy milk"}]}""")) { FinalText = "added" };
        var factory = Build(TodosYaml, store, adder, new ScriptedToolCallingChatClient() { FinalText = "echo1" });
        var session = factory.Create("call-1");

        await session.RunTurnAsync("track this", TestContext.Current.CancellationToken);

        var record = await store.GetAsync("call-1", TestContext.Current.CancellationToken);
        Assert.NotNull(record?.State);
        Assert.NotNull(record.State.WorkflowState);
        Assert.Empty(record.State.Providers);
    }

    [Fact]
    public async Task PickedProvidersWithoutABlob_AreIgnored_AdderStartsFresh()
    {
        InMemoryCallStore store = new();
        var adder = new ScriptedToolCallingChatClient() { FinalText = "added" };
        var adderRequests = new RequestCapturingChatClient(adder);
        var factory = Build(TodosYaml, store, adderRequests, new ScriptedToolCallingChatClient() { FinalText = "echo1" });

        CallSessionState fabricated = new()
        {
            Providers = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                [new TodoProvider().StateKeys[0]] = JsonDocument.Parse(
                    """{"items":[{"id":1,"title":"buy milk","isComplete":false}],"nextId":2}""").RootElement,
            },
        };

        var session = factory.Create("call-1", fabricated);
        await session.RunTurnAsync("anything new?", TestContext.Current.CancellationToken);

        Assert.DoesNotContain(
            adderRequests.Requests[0],
            message => message.Text.Contains("buy milk", StringComparison.Ordinal));
    }

    private static CallSessionFactory Build(string yaml, ICallStore store, IChatClient first, IChatClient second)
    {
        var document = ConfigurationLoader.LoadYaml(yaml);
        RoutingChatClientFactory chatClients = new(first);
        chatClients.Route("echo", second);
        chatClients.Route("second", second);
        var compiled = ConfigurationCompiler.Compile(
            document,
            new AgentCompilationContext(chatClients) { CallStore = store });

        return new CallSessionFactory(compiled, new GuardEvaluator(compiled.Configuration.Guards));
    }
}
