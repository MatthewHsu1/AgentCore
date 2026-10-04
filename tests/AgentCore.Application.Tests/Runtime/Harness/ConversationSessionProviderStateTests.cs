using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Sessions.Memory;
using AgentCore.Application.Tests.Fakes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Runtime.Harness
{
    /// <summary>
    /// The end-to-end path of a <c>todos:</c> item written during a turn lands in store
    /// 0's <c>Providers</c>, and a conversation that resumes — from the same store, or from a host checkpoint —
    /// gets it re-injected by the framework's own <see cref="TodoProvider"/>.
    /// </summary>
    public sealed class ConversationSessionProviderStateTests
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
            InMemoryConversationStore store = new();
            CompiledAgent compiled = Compile(TodosYaml, new TodoAddThenTextChatClient(), store);
            ConversationSessionFactory factory = new(compiled, new GuardEvaluator(compiled.Configuration.Guards));
            ConversationSession session = factory.Create("conversation-1");

            _ = await session.RunTurnAsync("please track this", TestContext.Current.CancellationToken);

            ConversationRecord? record = await store.GetAsync("conversation-1", TestContext.Current.CancellationToken);

            Assert.NotNull(record);
            Assert.NotNull(record.State);
            string key = Assert.Single(record.State.Providers.Keys);

            // The one literal: it pins the on-disk shape of the todos: state, everywhere else the key
            // comes from the real provider's own StateKeys.
            Assert.Equal("TodoProvider", key);
            Assert.Contains("buy milk", record.State.Providers[key].GetRawText(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task ASecondSessionFromTheSameFactoryAndStore_ReInjectsTheTodo()
        {
            InMemoryConversationStore store = new();
            TodoAddThenTextChatClient chatClient = new();
            CompiledAgent compiled = Compile(TodosYaml, chatClient, store);
            ConversationSessionFactory factory = new(compiled, new GuardEvaluator(compiled.Configuration.Guards));

            ConversationSession first = factory.Create("conversation-1");
            _ = await first.RunTurnAsync("please track this", TestContext.Current.CancellationToken);

            ConversationSession second = factory.Create("conversation-1");
            _ = await second.RunTurnAsync("what's on my list", TestContext.Current.CancellationToken);

            Assert.Contains(
                chatClient.Requests[^1],
                message => message.Text.Contains("buy milk", StringComparison.Ordinal));
        }

        [Fact]
        public async Task ASessionThatCaughtUpOnAnotherHostsTurn_RunsOnTheTodoThatTurnStored_AndKeepsIt()
        {
            InMemoryConversationStore store = new();
            SequencedChatClient hostA = new("a0", "a2");
            CompiledAgent compiledA = Compile(TodosYaml, hostA, store);
            CompiledAgent compiledB = Compile(TodosYaml, new TodoAddThenTextChatClient(), store);
            ConversationSession a = new ConversationSessionFactory(compiledA, new GuardEvaluator(compiledA.Configuration.Guards)).Create("conversation-1");
            ConversationSession b = new ConversationSessionFactory(compiledB, new GuardEvaluator(compiledB.Configuration.Guards)).Create("conversation-1");

            _ = await a.RunTurnAsync("hello", TestContext.Current.CancellationToken);
            _ = await b.RunTurnAsync("please track this", TestContext.Current.CancellationToken);
            _ = await a.RunTurnAsync("what's on my list", TestContext.Current.CancellationToken);
            await a.FlushTranscriptAsync();

            Assert.Contains(hostA.Requests[^1], message => message.Text.Contains("buy milk", StringComparison.Ordinal));
            ConversationRecord? record = await store.GetAsync("conversation-1", TestContext.Current.CancellationToken);
            Assert.Equal(3, record?.State?.NextTurnIndex);
            Assert.Contains("buy milk", record!.State!.Providers[new TodoProvider().StateKeys[0]].GetRawText(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task ASessionWhoseTurnTheStoreDropped_RunsOnTheTodoAnotherHostSavedUnderThatTurn_AndKeepsIt()
        {
            FlakyAppendConversationStore store = new();
            SequencedChatClient hostA = new("a0", "a1", "a2");
            CompiledAgent compiledA = Compile(TodosYaml, hostA, store);
            CompiledAgent compiledB = Compile(TodosYaml, new TodoAddThenTextChatClient(), store);
            ConversationSession a = new ConversationSessionFactory(compiledA, new GuardEvaluator(compiledA.Configuration.Guards)).Create("conversation-1");
            ConversationSession b = new ConversationSessionFactory(compiledB, new GuardEvaluator(compiledB.Configuration.Guards)).Create("conversation-1");

            _ = await a.RunTurnAsync("hello", TestContext.Current.CancellationToken);
            store.Down = true;
            _ = await a.RunTurnAsync("this turn is lost", TestContext.Current.CancellationToken);
            await a.FlushTranscriptAsync();
            store.Down = false;

            // B opens on the store, which never saw A's turn 1, so B saves its own turn 1.
            _ = await b.RunTurnAsync("please track this", TestContext.Current.CancellationToken);
            _ = await a.RunTurnAsync("what's on my list", TestContext.Current.CancellationToken);
            await a.FlushTranscriptAsync();

            Assert.Contains(hostA.Requests[^1], message => message.Text.Contains("buy milk", StringComparison.Ordinal));
            ConversationRecord? record = await store.GetAsync("conversation-1", TestContext.Current.CancellationToken);
            Assert.Equal(3, record?.State?.NextTurnIndex);
            Assert.Contains("buy milk", record!.State!.Providers[new TodoProvider().StateKeys[0]].GetRawText(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task AResumeFromAHostCheckpoint_ThroughAgentCoreAgent_ReInjectsTheTodo()
        {
            InMemoryConversationStore firstStore = new();
            TodoAddThenTextChatClient firstChatClient = new();
            CompiledAgent firstCompiled = Compile(TodosYaml, firstChatClient, firstStore);
            AgentCoreAgent firstAgent = AgentOver(
                new ConversationSessionFactory(firstCompiled, new GuardEvaluator(firstCompiled.Configuration.Guards)));

            AgentSession firstSession = await firstAgent.CreateSessionAsync("conversation-1", TestContext.Current.CancellationToken);
            _ = await firstAgent.RunAsync(
                "please track this", firstSession, cancellationToken: TestContext.Current.CancellationToken);

            JsonElement serialized = await firstAgent.SerializeSessionAsync(
                firstSession, cancellationToken: TestContext.Current.CancellationToken);

            // A fresh store: this proves the checkpoint carries the provider state on its own, not
            // because the conversation store still remembers the conversation.
            InMemoryConversationStore secondStore = new();
            SequencedChatClient secondChatClient = new("hello there.");
            CompiledAgent secondCompiled = Compile(TodosYaml, secondChatClient, secondStore);
            AgentCoreAgent secondAgent = AgentOver(
                new ConversationSessionFactory(secondCompiled, new GuardEvaluator(secondCompiled.Configuration.Guards)));

            AgentSession revived = await secondAgent.DeserializeSessionAsync(
                serialized, cancellationToken: TestContext.Current.CancellationToken);
            _ = await secondAgent.RunAsync(
                "what's on my list", revived, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains(
                secondChatClient.Requests[^1],
                message => message.Text.Contains("buy milk", StringComparison.Ordinal));
        }

        [Fact]
        public async Task AVersion99StoredState_DropsTheProviders_TheTodoNeverReachesTheRequest()
        {
            InMemoryConversationStore store = new();
            SequencedChatClient chatClient = new("hello there.");
            CompiledAgent compiled = Compile(TodosYaml, chatClient, store);
            ConversationSessionFactory factory = new(compiled, new GuardEvaluator(compiled.Configuration.Guards));

            ConversationSessionState fabricated = new()
            {
                Version = 99,
                Providers = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    [new TodoProvider().StateKeys[0]] = JsonDocument.Parse(
                        """{"items":[{"id":1,"title":"buy milk","isComplete":false}],"nextId":2}""").RootElement,
                },
            };

            ConversationSession session = factory.Create("conversation-1", fabricated);

            _ = await session.RunTurnAsync("what's on my list", TestContext.Current.CancellationToken);

            Assert.DoesNotContain(
                chatClient.Requests[^1],
                message => message.Text.Contains("buy milk", StringComparison.Ordinal));
        }

        [Fact]
        public async Task NoTodosBlock_AfterATurn_Store0HoldsNoProviders()
        {
            InMemoryConversationStore store = new();
            SequencedChatClient chatClient = new("hello there.");
            CompiledAgent compiled = Compile(NoTodosYaml, chatClient, store);
            ConversationSessionFactory factory = new(compiled, new GuardEvaluator(compiled.Configuration.Guards));
            ConversationSession session = factory.Create("conversation-1");

            _ = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            ConversationRecord? record = await store.GetAsync("conversation-1", TestContext.Current.CancellationToken);

            Assert.NotNull(record);
            Assert.NotNull(record.State);
            Assert.Empty(record.State.Providers);
        }

        private static CompiledAgent Compile(string yaml, IChatClient chatClient, IConversationStore store)
        {
            return ConfigurationCompiler.CompileAll(
                        ConfigurationLoader.LoadYaml(yaml),
                        new AgentCompilationContext(new FakeChatClientFactory(chatClient)) { ConversationStore = store })["main"];
        }

        private static AgentCoreAgent AgentOver(IConversationSessionFactory factory)
        {
            InMemoryConversationSessions sessions = new(
                new Dictionary<string, IConversationSessionFactory>(StringComparer.Ordinal) { ["main"] = factory },
                TimeSpan.FromMinutes(30),
                TimeProvider.System);

            return new AgentCoreAgent(sessions, "main");
        }

        /// <summary>
        /// Calls <c>todos_add {"todos":[{"title":"buy milk"}]}</c> on the very first request this
        /// instance ever answers, then answers text on every request after — regardless of whether the
        /// transcript already carries a tool result, so the same instance can drive two sessions of one
        /// conversation: the one that adds the todo, and the one that resumes it.
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

                int index = Interlocked.Increment(ref _calls) - 1;
                List<ChatMessage> transcript = [.. messages];
                lock (Requests)
                {
                    Requests.Add(transcript);
                }

                await Task.Yield();

                string responseId = Guid.NewGuid().ToString("N");
                AIFunction? tool = options?.Tools?.OfType<AIFunction>().FirstOrDefault();

                if (index == 0 && tool is not null)
                {
                    yield return new ChatResponseUpdate(
                        ChatRole.Assistant,
                        [new FunctionCallContent(
                            "conversation_1",
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
                await foreach (ChatResponseUpdate? update in GetStreamingResponseAsync(messages, options, cancellationToken)
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
}
