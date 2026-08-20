using System.Runtime.CompilerServices;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Runtime;
using AgentCore.Application.Transcript;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Transcript;

/// <summary>
/// Where store 1 is bound: on the compiled agent of rows 1 and 2, and on nothing else.
/// </summary>
/// <remarks>
/// <para>
/// The provider is bound once, at compile time, so no run has to name a conversation to keep it —
/// which is what lets <c>ThrowOnChatHistoryProviderConflict</c> stay at the framework default.
/// </para>
/// <para>
/// Rows 3 and 4 must stay unbound. A node is handed the call in the request messages the turn loop
/// renders, and it runs on a session the workflow made rather than the call's, so store 1 on a node
/// is a second history source keyed on a session it does not know.
/// </para>
/// <para>
/// Every test here runs offline: no network call and no API key.
/// </para>
/// </remarks>
public sealed class CompiledHistoryProviderTests
{
    private const string SingleAgentYaml =
        """
        apiVersion: agentcore/v1
        name: row-one
        agents:
          items:
            - { id: solo }
        """;

    private const string PolicyYaml =
        """
        apiVersion: agentcore/v1
        name: row-two
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
        policy:
          initial: talk
          stages:
            - { id: talk, agent: front, to: [ { stage: done, when: known } ] }
            - { id: done, agent: front, terminal: true }
        """;

    private const string PatternGraphYaml =
        """
        apiVersion: agentcore/v1
        name: row-three
        agents:
          items:
            - { id: researcher, model: { ref: researcher } }
            - { id: responder,  model: { ref: responder } }
        graph:
          pattern: sequential
          agents: [ researcher, responder ]
        """;

    private const string ExplicitGraphYaml =
        """
        apiVersion: agentcore/v1
        name: row-four
        agents:
          items:
            - { id: researcher, model: { ref: researcher } }
            - { id: responder,  model: { ref: responder } }
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

    // ---------------------------------------------------------------------------------------------
    // Hazard 3 and 4: which agents hold the provider, and that every row still constructs.
    // ---------------------------------------------------------------------------------------------
    [Theory]
    [InlineData(SingleAgentYaml)]
    [InlineData(PolicyYaml)]
    public void ARowThatCarriesHistoryOnItsSession_BindsStoreOneToEveryCompiledAgent(string yaml)
    {
        using ToolCallingChatClient client = new("hello");

        var compiled = Compile(yaml, client);

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

        var compiled = Compile(yaml, client);

        // A ChatClientAgent that is handed no provider builds the framework's own in-memory one, which
        // opens empty on each node session and never sees store 1. The fact is that store 1 is not
        // there, not that nothing is.
        Assert.NotEmpty(compiled.Agents);
        Assert.All(
            compiled.Agents.Values,
            agent => Assert.IsNotType<AgentCoreChatHistoryProvider>(ChatClientAgentOf(agent).ChatHistoryProvider));
    }

    // ---------------------------------------------------------------------------------------------
    // The safety check the workaround switched off.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task AModelThatKeepsTheHistoryItself_ConflictsWithStoreOne()
    {
        // A response that carries a conversation id is how a service says it keeps the history itself.
        // That and store 1 are two answers to one question, and the framework refuses both at once.
        // Switching this check off to buy something else — a telemetry attribute, say — leaves a call
        // whose model silently sees one message and no history.
        ServerSideHistoryChatClient client = new("hello");
        var compiled = Compile(SingleAgentYaml, client);
        var agent = compiled.Agents["solo"];
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        var conflict = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.RunAsync("hi", session, options: null, TestContext.Current.CancellationToken));

        Assert.Contains("ChatHistoryProvider", conflict.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // Hazard 1: a workflow node reads the call once.
    // ---------------------------------------------------------------------------------------------
    [Theory]
    [InlineData(PatternGraphYaml)]
    [InlineData(ExplicitGraphYaml)]
    public async Task AGraphRow_ReplaysTheCallToItsNodesExactlyOnce(string yaml)
    {
        RecordingChatClient researcher = new("looking into it");
        RecordingChatClient responder = new("it ships Friday");
        var session = CreateSession(yaml, researcher, responder);

        _ = await session.RunTurnAsync(FirstUtterance, TestContext.Current.CancellationToken);
        _ = await session.RunTurnAsync(SecondUtterance, TestContext.Current.CancellationToken);

        // One system message carries the whole call to a graph row, and it is the only way the call
        // reaches a node. Two copies of the caller's words in one request is what a second history
        // source on that node would look like.
        Assert.Equal(1, Mentions(researcher.Requests[1], FirstUtterance));
        Assert.Equal(1, Mentions(responder.Requests[1], FirstUtterance));
    }

    // ---------------------------------------------------------------------------------------------
    // Hazard 2: a delegated agent starts empty.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task ADelegatedAgent_NeverReadsTheCallersTranscript()
    {
        DelegatingChatClient front = new(FrontReply);
        RecordingChatClient specialist = new("the specialist answer");
        var session = CreateSession(PolicyYaml, front, specialist);

        _ = await session.RunTurnAsync(FirstUtterance, TestContext.Current.CancellationToken);
        _ = await session.RunTurnAsync(SecondUtterance, TestContext.Current.CancellationToken);

        // Agent-as-tool is call and return: the inner run gets a fresh AgentSession, and store 1 keys
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
        => request.Count(message => message.Contains(text, StringComparison.Ordinal));

    /// <summary>Reads the <see cref="ChatClientAgent"/> under whatever the compiler wrapped it in.</summary>
    private static ChatClientAgent ChatClientAgentOf(AIAgent agent)
    {
        var inner = agent.GetService<ChatClientAgent>();
        Assert.NotNull(inner);
        return inner;
    }

    private static CompiledAgent Compile(string yaml, IChatClient client)
        => ConfigurationCompiler.Compile(
            ConfigurationLoader.LoadYaml(yaml),
            new AgentCompilationContext(new FakeChatClientFactory(client)));

    /// <summary>Compiles one document over two named models and opens a call on it.</summary>
    private static CallSession CreateSession(string yaml, IChatClient first, IChatClient second)
    {
        RoutingChatClientFactory chatClients = new(first);
        chatClients.Route("researcher", first);
        chatClients.Route("front", first);
        chatClients.Route("responder", second);
        chatClients.Route("specialist", second);

        var compiled = ConfigurationCompiler.Compile(
            ConfigurationLoader.LoadYaml(yaml), new AgentCompilationContext(chatClients));

        return new CallSessionFactory(
            compiled,
            new GuardEvaluator(compiled.Configuration.Guards),
            extractor: null).Create();
    }

    /// <summary>Answers with a conversation id, the way a service that keeps the history does.</summary>
    private sealed class ServerSideHistoryChatClient : IChatClient
    {
        private readonly string _reply;

        public ServerSideHistoryChatClient(string reply) => _reply = reply;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, _reply)) { ConversationId = "server-side" });

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();

            var responseId = Guid.NewGuid().ToString("N");
            yield return new ChatResponseUpdate(ChatRole.Assistant, _reply)
            {
                ResponseId = responseId,
                MessageId = responseId,
                ConversationId = "server-side",
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>Answers with one fixed line, and keeps every request it was handed.</summary>
    private sealed class RecordingChatClient : IChatClient
    {
        private readonly string _reply;

        public RecordingChatClient(string reply) => _reply = reply;

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

            var responseId = Guid.NewGuid().ToString("N");
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

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

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
    /// <remarks>
    /// <see cref="ToolCallingChatClient"/> delegates once per call, because it stops as soon as the
    /// request carries any tool result. Store 1 of rows 1 and 2 keeps the finished tool pair, so on a
    /// second turn that client would never delegate again — and a fact about the SECOND delegation
    /// needs one that does. The rule here reads the last message instead: a request that ends on the
    /// caller delegates, and a request that ends on the tool result answers.
    /// </remarks>
    private sealed class DelegatingChatClient : IChatClient
    {
        private const string CallId = "call_1";

        /// <summary>The one argument <c>AsAIFunction()</c> generates for an agent that declares no schema.</summary>
        private static readonly Dictionary<string, object?> Arguments =
            new(StringComparer.Ordinal) { ["query"] = DelegatedQuestion };

        private readonly string _reply;

        public DelegatingChatClient(string reply) => _reply = reply;

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);
            await Task.Yield();

            var responseId = Guid.NewGuid().ToString("N");
            // The turn's context provider appends a system message below the caller's words, so the
            // caller's utterance is no longer the last message. Skip what the framework injected.
            var last = messages.LastOrDefault(message => message.Role != ChatRole.System);
            var tool = options?.Tools?.OfType<AIFunction>().FirstOrDefault();

            if (tool is not null && last?.Role == ChatRole.User)
            {
                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [new FunctionCallContent(CallId, tool.Name, Arguments)])
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
            await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false))
            {
                updates.Add(update);
            }

            return updates.ToChatResponse();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
