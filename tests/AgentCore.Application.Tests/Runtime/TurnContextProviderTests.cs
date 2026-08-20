using System.Text.Json;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Runtime;
using AgentCore.Application.State;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Transcript;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Runtime;

/// <summary>
/// The per-invocation seam: what one turn hands the model on top of the agent's own instructions.
/// </summary>
/// <remarks>
/// <para>
/// The compiled agent is a process singleton, so the provider bound to it is one too and may hold
/// nothing per call. It finds the turn on the flow of execution instead, and it applies what it finds
/// only to a run on the session that turn opened. That rule is what keeps a delegated run — which is
/// call and return on a session of its own — out of the turn's context.
/// </para>
/// <para>
/// Every test here runs offline: no network call and no API key.
/// </para>
/// </remarks>
public sealed class TurnContextProviderTests
{
    private const string ReminderYaml =
        """
        apiVersion: agentcore/v1
        name: reminder-context
        state:
          machineModel: { type: string, writer: extractor, description: the machine model }
        guards:
          identified: { "!!": [ { var: machineModel } ] }
        tools:
          - { id: ask_specialist, kind: agent, agent: specialist, description: Ask the specialist. }
        agents:
          defaults:
            model: { ref: reply }
          items:
            - { id: greeter, instructions: "greet the caller", tools: [ ask_specialist ] }
            - { id: specialist, model: { ref: specialist }, instructions: "answer the greeter" }
        policy:
          initial: greeting
          stages:
            - id: greeting
              agent: greeter
              to: [ { stage: close, when: identified } ]
            - id: close
              agent: specialist
              terminal: true
        """;

    private const string SingleAgentYaml =
        """
        apiVersion: agentcore/v1
        name: row-one
        agents:
          items:
            - { id: solo }
        """;

    private const string PatternGraphYaml =
        """
        apiVersion: agentcore/v1
        name: row-three
        agents:
          items:
            - { id: researcher }
            - { id: responder }
        graph:
          pattern: sequential
          agents: [ researcher, responder ]
        """;

    [Fact]
    public async Task TheProvider_HandsTheTurnsInstructionsToARunOnTheCallsSession()
    {
        StubSession session = new();
        using var scope = TurnContextScope.Enter(new TurnContext { Session = session, Instructions = "ask for the model" });

        var context = await InvokeAsync(session);

        // A message, not AIContext.Instructions. The framework folds Instructions into the system
        // message at the head of the prompt, where per-turn text caps the vendor's cacheable prefix.
        Assert.NotNull(context.Messages);
        var message = Assert.Single(context.Messages);
        Assert.Equal(ChatRole.System, message.Role);
        Assert.Equal("ask for the model", message.Text);
        Assert.Null(context.Instructions);
    }

    [Fact]
    public async Task TheProvider_HandsNothingToARunOnAnotherSession()
    {
        StubSession call = new();
        StubSession delegated = new();
        using var scope = TurnContextScope.Enter(new TurnContext { Session = call, Instructions = "ask for the model" });

        var context = await InvokeAsync(delegated);

        Assert.Null(context.Messages);
        Assert.Null(context.Instructions);
    }

    [Fact]
    public async Task TheProvider_HandsNothingWhenNoTurnIsOpen()
    {
        var context = await InvokeAsync(new StubSession());

        Assert.Null(context.Messages);
        Assert.Null(context.Instructions);
    }

    [Fact]
    public void TheProvider_FilesItsStateUnderAKeyOfItsOwn()
    {
        // Both providers go on the same agent, and ChatClientAgent throws at construction on a
        // duplicate key. This is the assertion that fails first if either type is renamed.
        Assert.Equal(["TurnContextProvider"], new TurnContextProvider().StateKeys);
        Assert.NotEqual(new AgentCoreChatHistoryProvider().StateKeys, new TurnContextProvider().StateKeys);
    }

    [Theory]
    [InlineData(ReminderYaml)]
    [InlineData(SingleAgentYaml)]
    [InlineData(PatternGraphYaml)]
    public void EveryCompiledAgent_CarriesTheProvider(string yaml)
    {
        using SequencedChatClient reply = new("hello there.");

        var compiled = ConfigurationCompiler.Compile(
            ConfigurationLoader.LoadYaml(yaml), new AgentCompilationContext(new FakeChatClientFactory(reply)));

        Assert.NotEmpty(compiled.Agents);
        Assert.All(
            compiled.Agents.Values,
            agent => Assert.Contains(
                ChatClientAgentOf(agent).AIContextProviders ?? [],
                provider => provider is TurnContextProvider));
    }

    [Fact]
    public async Task TheReminder_ReachesTheReplyAgentBelowTheTranscript()
    {
        using SequencedChatClient reply = new("hello there.");
        using SequencedChatClient specialist = new("the specialist answer");
        var session = CreateSession(reply, specialist);

        await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

        Assert.Contains(UnfilledSlotReminder.OpenTag, reply.SystemText(0), StringComparison.Ordinal);
        Assert.Contains("the machine model", reply.SystemText(0), StringComparison.Ordinal);

        // The agent's own instructions survive, and stay clean: the turn's context rides a message of
        // its own so the instructions block is byte-identical from one turn to the next.
        Assert.Equal("greet the caller", reply.Options[0]!.Instructions);
    }

    [Fact]
    public async Task TheReminder_NeverReachesADelegatedAgent()
    {
        // Agent-as-tool is call and return on a session of its own. The sub-agent is not the one
        // talking to the caller, so a reminder about what the caller still owes is not its business.
        using ToolCallingChatClient greeter = new(
            "hello there.",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["query"] = "check the order system" });
        using SequencedChatClient specialist = new("the specialist answer");
        var session = CreateSession(greeter, specialist);

        await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

        Assert.NotEmpty(specialist.Requests);
        Assert.All(
            Enumerable.Range(0, specialist.Requests.Count),
            request => Assert.DoesNotContain(
                UnfilledSlotReminder.OpenTag, specialist.SystemText(request), StringComparison.Ordinal));
    }

    /// <summary>Runs the provider the way the framework runs it, for one run on one session.</summary>
    private static async Task<AIContext> InvokeAsync(AgentSession session)
    {
#pragma warning disable MAAI001 // The context constructors are the framework's own experimental surface.
        AIContextProvider.InvokingContext context = new(StubAgent.Instance, session, new AIContext());
#pragma warning restore MAAI001
        return await new TurnContextProvider().InvokingAsync(context, TestContext.Current.CancellationToken);
    }

    /// <summary>Reads the <see cref="ChatClientAgent"/> under whatever the compiler wrapped it in.</summary>
    private static ChatClientAgent ChatClientAgentOf(AIAgent agent)
    {
        var inner = agent.GetService<ChatClientAgent>();
        Assert.NotNull(inner);
        return inner;
    }

    /// <summary>Compiles <see cref="ReminderYaml"/> over two named models and opens a call on it.</summary>
    private static CallSession CreateSession(IChatClient reply, IChatClient specialist)
    {
        RoutingChatClientFactory chatClients = new(reply);
        chatClients.Route("reply", reply);
        chatClients.Route("specialist", specialist);

        var compiled = ConfigurationCompiler.Compile(
            ConfigurationLoader.LoadYaml(ReminderYaml), new AgentCompilationContext(chatClients));

        return new CallSessionFactory(
            compiled,
            new GuardEvaluator(compiled.Configuration.Guards),
            extractor: null).Create();
    }

    private sealed class StubSession : AgentSession;

    /// <summary>Stands in for the agent the framework names on a context. Nothing here runs it.</summary>
    private sealed class StubAgent : AIAgent
    {
        public static StubAgent Instance { get; } = new();

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(
            CancellationToken cancellationToken = default)
            => new(new StubSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedState,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
