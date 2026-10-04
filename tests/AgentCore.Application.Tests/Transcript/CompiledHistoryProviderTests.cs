using AgentCore.TestSupport;
using System.Runtime.CompilerServices;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Transcript;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Transcript
{
    /// <summary>
    /// Where the message store is bound: on the compiled agent of rows 1 and 2, and on nothing else.
    /// </summary>
    public sealed class CompiledHistoryProviderTests
    {
        private const string SingleAgentYaml =
            """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: solo }
        entries:
          main:
            agent: solo
        """;

        private const string PolicyYaml =
            """
          apiVersion: agentcore/v1
          state:
            orderId: { type: string, writer: extractor, description: the order the caller asks about }
          guards:
            known: { var: orderId }
          tools:
            - { id: ask_specialist, kind: agent, agent: specialist, description: Ask the specialist. }
          agents:
            items:
              - { id: front, model: { ref: front }, tools: [ ask_specialist ] }
              - { id: specialist, model: { ref: specialist } }
          entries:
            main:
              policy:
                initial: talk
                stages:
                  - { id: talk, agent: front, to: [ { stage: done, when: known } ] }
                  - { id: done, agent: front, terminal: true }
          """;

        private const string PatternGraphYaml =
            """
          apiVersion: agentcore/v1
          agents:
            items:
              - { id: researcher, model: { ref: researcher } }
              - { id: responder,  model: { ref: responder } }
          entries:
            main:
              graph:
                pattern: sequential
                agents: [ researcher, responder ]
          """;

        private const string ExplicitGraphYaml =
            """
          apiVersion: agentcore/v1
          agents:
            items:
              - { id: researcher, model: { ref: researcher } }
              - { id: responder,  model: { ref: responder } }
          entries:
            main:
              graph:
                nodes:
                  - { id: route,  agent: researcher, start: true }
                  - { id: answer, agent: responder,  output: true }
                edges:
                  - { from: route, to: answer }
          """;

        /// <summary>What the caller says on the first turn of every multi-turn fact here.</summary>
        private const string FirstUtterance = "where is my order";

        /// <summary>What the caller says on the second turn.</summary>
        private const string SecondUtterance = "and the second one?";

        /// <summary>What the delegating agent asks the sub-agent, on both turns.</summary>
        private const string DelegatedQuestion = "check the order system";

        /// <summary>What the delegating agent tells the caller once the sub-agent has answered.</summary>
        private const string FrontReply = "your order is on its way";

        [Theory]
        [InlineData(SingleAgentYaml)]
        [InlineData(PolicyYaml)]
        public void ARowThatCarriesHistoryOnItsSession_BindsStoreOneToEveryCompiledAgent(string yaml)
        {
            using ToolCallingChatClient client = new("hello");

            CompiledAgent compiled = Compile(yaml, client);

            Assert.NotEmpty(compiled.Agents);
            Assert.All(
                compiled.Agents.Values,
                agent => Assert.Same(compiled.History, ChatClientAgentOf(agent).ChatHistoryProvider));
        }

        [Theory]
        [InlineData(PatternGraphYaml)]
        [InlineData(ExplicitGraphYaml)]
        public void AGraphRow_BindsStoreOneToNoNodeAgent(string yaml)
        {
            using ToolCallingChatClient client = new("hello");

            CompiledAgent compiled = Compile(yaml, client);

            // A ChatClientAgent that is handed no provider builds the framework's own in-memory one, which
            // opens empty on each node session and never sees the message store. The fact is that message store is not
            // there, not that nothing is.
            Assert.NotEmpty(compiled.Agents);
            Assert.All(
                compiled.Agents.Values,
                agent => Assert.IsNotType<AgentCoreChatHistoryProvider>(ChatClientAgentOf(agent).ChatHistoryProvider));
        }

        [Fact]
        public async Task AModelThatKeepsTheHistoryItself_ConflictsWithStoreOne()
        {
            // A response that carries a conversation id is how a service says it keeps the history itself.
            // That and the message store are two answers to one question, and the framework refuses both at once.
            // Switching this check off to buy something else — a telemetry attribute, say — leaves a conversation
            // whose model silently sees one message and no history.
            ServerSideHistoryChatClient client = new("hello");
            CompiledAgent compiled = Compile(SingleAgentYaml, client);
            AIAgent agent = compiled.Agents["solo"];
            AgentSession session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

            InvalidOperationException conflict = await Assert.ThrowsAsync<InvalidOperationException>(
                () => agent.RunAsync("hi", session, options: null, TestContext.Current.CancellationToken));

            Assert.Contains("ChatHistoryProvider", conflict.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(PatternGraphYaml)]
        [InlineData(ExplicitGraphYaml)]
        public async Task AGraphRow_ReplaysTheConversationToItsNodesExactlyOnce(string yaml)
        {
            RecordingChatClient researcher = new("looking into it");
            RecordingChatClient responder = new("it ships Friday");
            ConversationSession session = CreateSession(yaml, researcher, responder);

            _ = await session.RunTurnAsync(FirstUtterance, TestContext.Current.CancellationToken);
            _ = await session.RunTurnAsync(SecondUtterance, TestContext.Current.CancellationToken);

            // One system message carries the whole call to a graph row, and it is the only way the conversation
            // reaches a node. Two copies of the caller's words in one request is what a second history
            // source on that node would look like.
            Assert.Equal(1, Mentions(researcher.Requests[1], FirstUtterance));
            Assert.Equal(1, Mentions(responder.Requests[1], FirstUtterance));
        }

        [Fact]
        public async Task ADelegatedAgent_NeverReadsTheCallersTranscript()
        {
            DelegatingChatClient front = new(FrontReply);
            RecordingChatClient specialist = new("the specialist answer");
            ConversationSession session = CreateSession(PolicyYaml, front, specialist);

            _ = await session.RunTurnAsync(FirstUtterance, TestContext.Current.CancellationToken);
            _ = await session.RunTurnAsync(SecondUtterance, TestContext.Current.CancellationToken);

            // Agent-as-tool is call and return: the inner run gets a fresh AgentSession, and the message store keys
            // on the session, so the second delegation opens on an empty history exactly like the first.
            Assert.Equal(2, specialist.Requests.Count);
            Assert.All(
                specialist.Requests,
                request =>
                {
                    Assert.Equal(1, Mentions(request, DelegatedQuestion));
                    Assert.Equal(0, Mentions(request, FirstUtterance));
                    Assert.Equal(0, Mentions(request, FrontReply));
                });
        }

        /// <summary>Counts the messages of one recorded request that carry a piece of text.</summary>
        private static int Mentions(IEnumerable<string> request, string text)
        {
            return request.Count(message => message.Contains(text, StringComparison.Ordinal));
        }

        /// <summary>Reads the <see cref="ChatClientAgent"/> under whatever the compiler wrapped it in.</summary>
        private static ChatClientAgent ChatClientAgentOf(AIAgent agent)
        {
            ChatClientAgent? inner = agent.GetService<ChatClientAgent>();
            Assert.NotNull(inner);
            return inner;
        }

        private static CompiledAgent Compile(string yaml, IChatClient client)
        {
            return ConfigurationCompiler.CompileAll(
                        ConfigurationLoader.LoadYaml(yaml),
                        new AgentCompilationContext(new FakeChatClientFactory(client)))["main"];
        }

        /// <summary>Compiles one document over two named models and opens a conversation on it.</summary>
        private static ConversationSession CreateSession(string yaml, IChatClient first, IChatClient second)
        {
            RoutingChatClientFactory chatClients = new(first);
            _ = chatClients.Route("researcher", first);
            _ = chatClients.Route("front", first);
            _ = chatClients.Route("responder", second);
            _ = chatClients.Route("specialist", second);

            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                ConfigurationLoader.LoadYaml(yaml), new AgentCompilationContext(chatClients))["main"];

            return new ConversationSessionFactory(
                compiled,
                new GuardEvaluator(compiled.Configuration.Guards),
                extractor: null).Create();
        }

        /// <summary>Answers with a conversation id, the way a service that keeps the history does.</summary>
        private sealed class ServerSideHistoryChatClient(string reply) : IChatClient
        {
            private readonly string _reply = reply;

            public Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(
                                new ChatResponse(new ChatMessage(ChatRole.Assistant, _reply)) { ConversationId = "server-side" });
            }

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.Yield();

                string responseId = Guid.NewGuid().ToString("N");
                yield return new ChatResponseUpdate(ChatRole.Assistant, _reply)
                {
                    ResponseId = responseId,
                    MessageId = responseId,
                    ConversationId = "server-side",
                };
            }

            public object? GetService(Type serviceType, object? serviceKey = null)
            {
                return null;
            }

            public void Dispose()
            {
            }
        }

        /// <summary>Answers with one fixed line, and keeps every request it was handed.</summary>
        private sealed class RecordingChatClient(string reply) : IChatClient
        {
            private readonly string _reply = reply;

            /// <summary>Gets each request, one role-prefixed line per message, in the order they arrived.</summary>
            public List<List<string>> Requests { get; } = [];

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(messages);
                Record(messages);
                await Task.Yield();

                string responseId = Guid.NewGuid().ToString("N");
                yield return new ChatResponseUpdate(ChatRole.Assistant, _reply)
                {
                    ResponseId = responseId,
                    MessageId = responseId,
                };
            }

            public Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(messages);
                Record(messages);
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _reply)));
            }

            public object? GetService(Type serviceType, object? serviceKey = null)
            {
                return null;
            }

            public void Dispose()
            {
            }

            private void Record(IEnumerable<ChatMessage> messages)
            {
                lock (Requests)
                {
                    Requests.Add([.. messages.Select(message => $"{message.Role}:{message.Text}")]);
                }
            }
        }

        /// <summary>
        /// Calls the one tool it is offered on every turn, then answers.
        /// </summary>
        private sealed class DelegatingChatClient(string reply) : IChatClient
        {
            private const string ConversationId = "conversation_1";

            /// <summary>The one argument <c>AsAIFunction()</c> generates for an agent that declares no schema.</summary>
            private static readonly Dictionary<string, object?> Arguments =
                new(StringComparer.Ordinal) { ["query"] = DelegatedQuestion };

            private readonly string _reply = reply;

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(messages);
                await Task.Yield();

                string responseId = Guid.NewGuid().ToString("N");
                // The turn's context provider appends a system message below the caller's words, so the
                // caller's utterance is no longer the last message. Skip what the framework injected.
                ChatMessage? last = messages.LastOrDefault(message => message.Role != ChatRole.System);
                AIFunction? tool = options?.Tools?.OfType<AIFunction>().FirstOrDefault();

                if (tool is not null && last?.Role == ChatRole.User)
                {
                    yield return new ChatResponseUpdate(
                        ChatRole.Assistant,
                        [new FunctionCallContent(ConversationId, tool.Name, Arguments)])
                    {
                        ResponseId = responseId,
                        MessageId = responseId,
                    };

                    yield break;
                }

                yield return new ChatResponseUpdate(ChatRole.Assistant, _reply)
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
                return null;
            }

            public void Dispose()
            {
            }
        }
    }
}
