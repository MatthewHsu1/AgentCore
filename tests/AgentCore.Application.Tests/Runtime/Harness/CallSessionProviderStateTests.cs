using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Calls;
using AgentCore.Application.Calls.Memory;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Runtime.Harness;
using AgentCore.Application.Tests.Fakes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Runtime.Harness;

/// <summary>
/// The end-to-end path of harness step 4: a <c>todos:</c> item written during a turn lands in store
/// 0's <c>Providers</c>, and a call that resumes — from the same store, or from a host checkpoint —
/// gets it re-injected by the framework's own <see cref="TodoProvider"/>.
/// </summary>
public sealed class CallSessionProviderStateTests
{
    private const string TodosYaml =
        """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "track todos", todos: true }
        entries:
          main:
            agent: only
        """;

    private const string NoTodosYaml =
        """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "just talk" }
        entries:
          main:
            agent: only
        """;

    [Fact]
    public async Task AfterATurn_Store0HoldsOnlyTheTodoProviderKey_WithTheTodoInIt()
    {
        InMemoryCallStore store = new();
        var compiled = Compile(TodosYaml, new TodoAddThenTextChatClient(), store);
        var factory = new CallSessionFactory(compiled, new GuardEvaluator(compiled.Configuration.Guards));
        var session = factory.Create("call-1");

        await session.RunTurnAsync("please track this", TestContext.Current.CancellationToken);

        var record = await store.GetAsync("call-1", TestContext.Current.CancellationToken);

        Assert.NotNull(record);
        Assert.NotNull(record.State);
        var key = Assert.Single(record.State.Providers.Keys);

        // The one literal: it pins the on-disk shape of the todos: state, everywhere else the key
        // comes from the real provider's own StateKeys.
        Assert.Equal("TodoProvider", key);
        Assert.Contains("buy milk", record.State.Providers[key].GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASecondSessionFromTheSameFactoryAndStore_ReInjectsTheTodo()
    {
        InMemoryCallStore store = new();
        TodoAddThenTextChatClient chatClient = new();
        var compiled = Compile(TodosYaml, chatClient, store);
        var factory = new CallSessionFactory(compiled, new GuardEvaluator(compiled.Configuration.Guards));

        var first = factory.Create("call-1");
        await first.RunTurnAsync("please track this", TestContext.Current.CancellationToken);

        var second = factory.Create("call-1");
        await second.RunTurnAsync("what's on my list", TestContext.Current.CancellationToken);

        Assert.Contains(
            chatClient.Requests[^1],
            message => message.Text.Contains("buy milk", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AResumeFromAHostCheckpoint_ThroughAgentCoreAgent_ReInjectsTheTodo()
    {
        InMemoryCallStore firstStore = new();
        TodoAddThenTextChatClient firstChatClient = new();
        var firstCompiled = Compile(TodosYaml, firstChatClient, firstStore);
        AgentCoreAgent firstAgent = new(
            new CallSessionFactory(firstCompiled, new GuardEvaluator(firstCompiled.Configuration.Guards)), "main");

        var firstSession = await firstAgent.CreateSessionAsync(
            "call-1", TestContext.Current.CancellationToken);
        await firstAgent.RunAsync(
            "please track this", firstSession, cancellationToken: TestContext.Current.CancellationToken);

        var serialized = await firstAgent.SerializeSessionAsync(
            firstSession, cancellationToken: TestContext.Current.CancellationToken);

        // A fresh store: this proves the checkpoint carries the provider state on its own, not
        // because store 0 still remembers the call.
        InMemoryCallStore secondStore = new();
        SequencedChatClient secondChatClient = new("hello there.");
        var secondCompiled = Compile(TodosYaml, secondChatClient, secondStore);
        AgentCoreAgent secondAgent = new(
            new CallSessionFactory(secondCompiled, new GuardEvaluator(secondCompiled.Configuration.Guards)), "main");

        var revived = await secondAgent.DeserializeSessionAsync(
            serialized, cancellationToken: TestContext.Current.CancellationToken);
        await secondAgent.RunAsync(
            "what's on my list", revived, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(
            secondChatClient.Requests[^1],
            message => message.Text.Contains("buy milk", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AVersion99StoredState_DropsTheProviders_TheTodoNeverReachesTheRequest()
    {
        InMemoryCallStore store = new();
        SequencedChatClient chatClient = new("hello there.");
        var compiled = Compile(TodosYaml, chatClient, store);
        var factory = new CallSessionFactory(compiled, new GuardEvaluator(compiled.Configuration.Guards));

        CallSessionState fabricated = new()
        {
            Version = 99,
            Providers = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                [new TodoProvider().StateKeys[0]] = JsonDocument.Parse(
                    """{"items":[{"id":1,"title":"buy milk","isComplete":false}],"nextId":2}""").RootElement,
            },
        };

        var session = factory.Create("call-1", fabricated);

        await session.RunTurnAsync("what's on my list", TestContext.Current.CancellationToken);

        Assert.DoesNotContain(
            chatClient.Requests[^1],
            message => message.Text.Contains("buy milk", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoTodosBlock_AfterATurn_Store0HoldsNoProviders()
    {
        InMemoryCallStore store = new();
        SequencedChatClient chatClient = new("hello there.");
        var compiled = Compile(NoTodosYaml, chatClient, store);
        var factory = new CallSessionFactory(compiled, new GuardEvaluator(compiled.Configuration.Guards));
        var session = factory.Create("call-1");

        await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

        var record = await store.GetAsync("call-1", TestContext.Current.CancellationToken);

        Assert.NotNull(record);
        Assert.NotNull(record.State);
        Assert.Empty(record.State.Providers);
    }

    private static CompiledAgent Compile(string yaml, IChatClient chatClient, ICallStore store)
        => ConfigurationCompiler.CompileAll(
            ConfigurationLoader.LoadYaml(yaml),
            new AgentCompilationContext(new FakeChatClientFactory(chatClient)) { CallStore = store })["main"];

    /// <summary>
    /// Calls <c>todos_add {"todos":[{"title":"buy milk"}]}</c> on the very first request this
    /// instance ever answers, then answers text on every request after — regardless of whether the
    /// transcript already carries a tool result, so the same instance can drive two sessions of one
    /// call: the one that adds the todo, and the one that resumes it.
    /// </summary>
    private sealed class TodoAddThenTextChatClient : IChatClient
    {
        private int _calls;

        public List<List<ChatMessage>> Requests { get; } = [];

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);

            var index = Interlocked.Increment(ref _calls) - 1;
            var transcript = messages.ToList();
            lock (Requests)
            {
                Requests.Add(transcript);
            }

            await Task.Yield();

            var responseId = Guid.NewGuid().ToString("N");
            var tool = options?.Tools?.OfType<AIFunction>().FirstOrDefault();

            if (index == 0 && tool is not null)
            {
                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [new FunctionCallContent(
                        "call_1",
                        tool.Name,
                        new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["todos"] = new List<object>
                            {
                                new Dictionary<string, object?>(StringComparer.Ordinal) { ["title"] = "buy milk" },
                            },
                        })])
                {
                    ResponseId = responseId,
                    MessageId = responseId,
                };
                yield break;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, "hello there.")
            {
                ResponseId = responseId,
                MessageId = responseId,
            };
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            List<ChatResponseUpdate> updates = [];
            await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false))
            {
                updates.Add(update);
            }

            return updates.ToChatResponse();
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
            // Nothing to release.
        }
    }
}
