using System.Runtime.CompilerServices;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Runtime;
using AgentCore.Application.State;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Knowledge.Fakes;
using AgentCore.Domain.Knowledge;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Runtime;

/// <summary>
/// Channel 1 (§7), driven through two real <see cref="CallSession"/> turns rather than by seeding the
/// ambient — K35's ordering is the thing under test: a near-tie the linker finds on turn 1 must reach
/// the model only on turn 2's own invocation, and never on a delegated sub-agent's.
/// </summary>
public sealed class CallSessionClarificationTests
{
    private const string AppliesToDescription = "The model, as printed on the machine.";

    private const string Yaml =
        """
        apiVersion: agentcore/v1
        name: clarification-two-turn
        state:
          applies_to:
            type: string
            writer: extractor
            description: "The model, as printed on the machine."
            vocabulary: { from: knowledge }
        extractor:
          model: { ref: fill }
          when: after_reply
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          knowledge:
            kind: qdrant
            collection: kb
            fields: { body: text }
            scope:
              template: "facets.{key}"
              fromState: [applies_to]
              wildcard: { value: "*", facets: [applies_to] }
            ambiguity: { maxCandidates: 6, maxAsks: 2 }
        agents:
          items:
            - id: only
        """;

    private const string PrefetchYaml =
        """
        apiVersion: agentcore/v1
        name: clarification-two-turn-prefetch
        state:
          applies_to:
            type: string
            writer: extractor
            description: "The model, as printed on the machine."
            vocabulary: { from: knowledge }
        extractor:
          model: { ref: fill }
          when: after_reply
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          knowledge:
            kind: qdrant
            collection: kb
            fields: { body: text }
            scope:
              template: "facets.{key}"
              fromState: [applies_to]
              wildcard: { value: "*", facets: [applies_to] }
            ambiguity: { maxCandidates: 6, maxAsks: 2 }
        agents:
          items:
            - id: only
              knowledge: { mode: prefetch, scoped: false }
        """;

    private const string DelegationYaml =
        """
        apiVersion: agentcore/v1
        name: clarification-delegation
        state:
          applies_to:
            type: string
            writer: extractor
            description: "The model, as printed on the machine."
            vocabulary: { from: knowledge }
        extractor:
          model: { ref: fill }
          when: after_reply
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          knowledge:
            kind: qdrant
            collection: kb
            fields: { body: text }
            scope:
              template: "facets.{key}"
              fromState: [applies_to]
              wildcard: { value: "*", facets: [applies_to] }
            ambiguity: { maxCandidates: 6, maxAsks: 2 }
        tools:
          - { id: ask_specialist, kind: agent, agent: specialist, description: Ask the specialist. }
        agents:
          defaults:
            model: { ref: reply }
          items:
            - id: only
              tools: [ ask_specialist ]
            - id: specialist
              model: { ref: specialist }
        policy:
          initial: greeting
          stages:
            - { id: greeting, agent: only }
        """;

    [Fact]
    public async Task NearTieOnTurn1_TurnTwosInvocationCarriesTheInstruction()
    {
        using SequencedChatClient reply = new(
            "I don't have that on file yet.",
            """{"applies_to":"the CT900"}""",
            "let me check on that for you.",
            "{}");

        var session = Build(reply);

        await session.RunTurnAsync("I have a CT900 and a CT900ENT", TestContext.Current.CancellationToken);

        // Turn 1: nothing was pending before the extractor ran, so the caller's own turn carries no
        // clarification. It is the near-tie itself, found by the linker after the model already
        // answered — the linker's Ambiguous outcome does not retract what was just said (K16, K29).
        Assert.Equal(string.Empty, reply.SystemText(0));

        await session.RunTurnAsync("okay, thanks", TestContext.Current.CancellationToken);

        Assert.Equal(
            ClarificationText.Instruction(AppliesToDescription, ["CT900", "CT900ENT"], 6, first: true),
            reply.SystemText(2));
    }

    [Fact]
    public async Task SameNearTie_InPrefetchMode_OnATurnWithNoToolCall_TheClarificationStillArrives()
    {
        // K29: the guard reads TurnContextScope, not the knowledge provider's own mode. Bound with a
        // real StubKnowledgePort so this also proves the instruction rides alongside a second real
        // provider's own card, rather than replacing it (K16).
        using SequencedChatClient reply = new(
            "I don't have that on file yet.",
            """{"applies_to":"the CT900"}""",
            "let me check on that for you.",
            "{}");

        var port = new StubKnowledgePort([Card("a")]);
        var session = Build(reply, PrefetchYaml, port);

        await session.RunTurnAsync("I have a CT900 and a CT900ENT", TestContext.Current.CancellationToken);
        await session.RunTurnAsync("okay, thanks", TestContext.Current.CancellationToken);

        // Prefetch mode means the model never calls a search tool at all, so this is also the "turn
        // where the model calls no tool" half of the clause. The prefetch search itself ran (Calls
        // is 2, once per turn) with no tool round involved anywhere.
        Assert.Equal(2, port.Calls);

        var turn2 = reply.Requests[2];
        Assert.Contains(turn2, message => message.Text.Contains("card a", StringComparison.Ordinal));
        Assert.Contains(
            turn2,
            message => message.Text.Equals(
                ClarificationText.Instruction(AppliesToDescription, ["CT900", "CT900ENT"], 6, first: true),
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task DelegatedRun_CarriesNoInstruction_AndCountsNothing()
    {
        // K29: every kind: agent delegation is a fresh RunAsync on a fresh session, so the same
        // session guard that protects a graph row protects a sub-agent too.
        ScriptedDelegatingChatClient mainAgent = new(
            (ToolName: null, Text: "okay, one moment."),
            (ToolName: "ask_specialist", Text: string.Empty),
            (ToolName: null, Text: "here is what the specialist said."));
        SequencedChatClient specialist = new("the specialist's answer.");
        SequencedChatClient extractor = new("""{"applies_to":"the CT900"}""", "{}");

        RoutingChatClientFactory chatClients = new(mainAgent);
        chatClients.Route("reply", mainAgent);
        chatClients.Route("specialist", specialist);
        chatClients.Route("fill", extractor);

        var compiled = ConfigurationCompiler.Compile(
            ConfigurationLoader.LoadYaml(DelegationYaml), new AgentCompilationContext(chatClients));
        var stateExtractor = CallSessionFactory.CreateExtractor(compiled, chatClients);
        VocabularyCache vocabulary = new();
        vocabulary.Replace("applies_to", ["CT900", "CT900ENT"], maxValues: 2000);

        var session = new CallSessionFactory(
            compiled, new GuardEvaluator(compiled.Configuration.Guards), stateExtractor, vocabulary: vocabulary)
            .Create("call-delegation");

        await session.RunTurnAsync("I have a CT900 and a CT900ENT", TestContext.Current.CancellationToken);
        await session.RunTurnAsync("ask the specialist", TestContext.Current.CancellationToken);

        const string named = "is not yet known";

        // "one injected message" is a fact about ONE ClarificationProvider.InvokingAsync call, proven
        // directly (with real access to the counter) by ClarificationProviderTests. The framework
        // resends the base message list on every round of one RunAsync's tool loop, so the same
        // injected line legitimately reaches the model more than once here — turn 2 calls the tool
        // and then answers, two rounds of the same invocation. What only a real delegated run can
        // prove is the other half: the specialist's own nested RunAsync must see none of it, on any
        // of its own rounds, even though the same ClarificationProvider is bound to it too.
        Assert.Equal(string.Empty, mainAgent.SystemText(0));
        Assert.Contains(named, mainAgent.SystemText(1), StringComparison.Ordinal);
        Assert.Contains(named, mainAgent.SystemText(2), StringComparison.Ordinal);

        Assert.NotEmpty(specialist.Requests);
        for (var request = 0; request < specialist.Requests.Count; request++)
        {
            Assert.DoesNotContain(named, specialist.SystemText(request), StringComparison.Ordinal);
        }
    }

    private static CallSession Build(SequencedChatClient reply, string yaml = Yaml, StubKnowledgePort? port = null)
    {
        var chatClients = new FakeChatClientFactory(reply);
        var compiled = ConfigurationCompiler.Compile(
            ConfigurationLoader.LoadYaml(yaml),
            new AgentCompilationContext(chatClients) { Knowledge = port });

        var extractor = CallSessionFactory.CreateExtractor(compiled, chatClients);
        VocabularyCache vocabulary = new();
        vocabulary.Replace("applies_to", ["CT900", "CT900ENT"], maxValues: 2000);

        return new CallSessionFactory(
            compiled, new GuardEvaluator(compiled.Configuration.Guards), extractor, vocabulary: vocabulary)
            .Create("call-1");
    }

    private static KnowledgeCard Card(string id)
        => new()
        {
            CardId = id,
            Text = "card " + id,
            Authority = 3,
            SourceRef = "ct900-om",
            SourceLocator = "p.27",
            Score = 0.87,
            ViaLink = false,
        };

    /// <summary>
    /// A model that calls a scripted tool name (or none) on each request, and records the full
    /// message list of every request it answered — <see cref="SequencedChatClient"/> plus
    /// <see cref="ToolCallingChatClient"/>'s tool-emitting behaviour, combined, because the delegation
    /// row needs both: a deterministic per-call script (turn 1 must not call the tool) and full
    /// message visibility (to read the system text a real provider chain produced).
    /// </summary>
    private sealed class ScriptedDelegatingChatClient : IChatClient
    {
        private readonly (string? ToolName, string Text)[] _script;
        private int _calls;

        public ScriptedDelegatingChatClient(params (string? ToolName, string Text)[] script) => _script = script;

        public List<List<ChatMessage>> Requests { get; } = [];

        public string SystemText(int request)
        {
            lock (Requests)
            {
                return string.Join(
                    '\n',
                    Requests[request].Where(message => message.Role == ChatRole.System).Select(message => message.Text));
            }
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);

            var index = Interlocked.Increment(ref _calls) - 1;
            lock (Requests)
            {
                Requests.Add([.. messages]);
            }

            await Task.Yield();

            var (toolName, text) = _script[Math.Min(index, _script.Length - 1)];
            var responseId = Guid.NewGuid().ToString("N");

            if (toolName is not null)
            {
                // AgentDelegationTool.Create wraps the inner agent through AsAIFunction(), which
                // generates a required string argument named "query". A call with no arguments fails
                // schema validation before the specialist is ever reached.
                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [new FunctionCallContent(
                        $"call_{index}",
                        toolName,
                        new Dictionary<string, object?>(StringComparer.Ordinal) { ["query"] = "check the model" })])
                {
                    ResponseId = responseId,
                    MessageId = responseId,
                };
                yield break;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, text)
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
