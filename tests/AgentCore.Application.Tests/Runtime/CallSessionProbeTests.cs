using System.Runtime.CompilerServices;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Knowledge;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.State;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Domain.Knowledge;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Runtime;

/// <summary>
/// §8's probe, and §7's channel 1, driven through real <see cref="CallSession"/> turns rather than by
/// seeding the ambient: the genuinely two-turn rows (K43's latch reset against K22's persistent
/// counter) and the genuinely graph-shaped row (K39) — both need the real turn loop's own
/// <c>BeginTurn</c> and row-kind wiring, which <c>KnowledgeProbeTests</c>'s ambient-level tests
/// cannot exercise.
/// </summary>
public sealed class CallSessionProbeTests
{
    private const string AppliesToDescription = "The model, as printed on the machine.";

    private const string BrandDescription = "The brand of the caller's machine.";

    /// <summary>Two agents, run in order. Neither is scoped for search; both share channel 1.</summary>
    private const string GraphYaml =
        """
        apiVersion: agentcore/v1
        name: probe-graph
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
            - { id: researcher, model: { ref: researcher }, instructions: "look things up" }
            - { id: responder,  model: { ref: responder },  instructions: "answer the caller" }
        graph:
          pattern: sequential
          agents: [ researcher, responder ]
        """;

    /// <summary>One tool-mode, scoped agent over two droppable facets, so K33 never blocks the drop.</summary>
    private const string ProbeYaml =
        """
        apiVersion: agentcore/v1
        name: probe-two-turn
        state:
          applies_to:
            type: string
            writer: extractor
            description: "The model, as printed on the machine."
            vocabulary: { from: knowledge }
          brand:
            type: string
            writer: extractor
            description: "The brand of the caller's machine."
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
              template: "{key}"
              fromState: [applies_to, brand]
              wildcard: { value: "*", facets: [applies_to, brand] }
            ambiguity: { maxCandidates: 6, maxAsks: 2 }
        agents:
          defaults:
            model: { ref: reply }
          items:
            - id: only
              knowledge: { mode: tool, scoped: true }
        """;

    [Fact]
    public async Task K39_GraphDocument_Channel1SaysNothing_OnEitherTurn()
    {
        // K39: channel 1's session guard passes only on a row whose session carries history -- rows 1
        // and 2. A graph row's participants each run on a session that is never the call's own, so the
        // clarification a near-tie arms on turn 1 must stay silent for both nodes on turn 2 as well,
        // where CallSessionClarificationTests proves it speaks on a row 1/2 document.
        const string named = "is not yet known";

        using ScriptedChatClient researcher = new("Let me check the order system.");
        using ScriptedChatClient responder = new("Order 41 ships Friday.");
        SequencedChatClient extractor = new("""{"applies_to":"the CT900"}""", "{}");

        RoutingChatClientFactory chatClients = new(researcher);
        chatClients.Route("researcher", researcher);
        chatClients.Route("responder", responder);
        chatClients.Route("fill", extractor);

        var compiled = ConfigurationCompiler.Compile(
            ConfigurationLoader.LoadYaml(GraphYaml), new AgentCompilationContext(chatClients));
        var stateExtractor = CallSessionFactory.CreateExtractor(compiled, chatClients);
        VocabularyCache vocabulary = new();
        vocabulary.Replace("applies_to", ["CT900", "CT900ENT"], maxValues: 2000);

        var session = new CallSessionFactory(
            compiled, new GuardEvaluator(compiled.Configuration.Guards), stateExtractor, vocabulary: vocabulary)
            .Create("call-graph-probe");

        await session.RunTurnAsync("I have a CT900 and a CT900ENT", TestContext.Current.CancellationToken);
        await session.RunTurnAsync("okay, thanks", TestContext.Current.CancellationToken);

        Assert.DoesNotContain(named, researcher.FullText, StringComparison.Ordinal);
        Assert.DoesNotContain(named, responder.FullText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoTurns_TheProbeAlwaysThrows_TheSecondTurnStillRunsAFreshProbeSearch()
    {
        // K43's latch is per-turn (cleared by BeginTurn); K22's probeAsks counter is not. A second
        // turn that attempts a genuinely new probe search -- rather than replaying turn one's stored
        // failure -- is what proves BeginTurn actually ran, wired through the real turn loop rather
        // than asserted directly against Clarifications (KnowledgeProbeTests already covers that unit
        // in isolation, without needing a live CallSession).
        var narrowedCalls = 0;
        var port = new ThrowingOnNarrowedScopePort(() => Interlocked.Increment(ref narrowedCalls));

        ScriptedToolCallingChatClient reply = new(
            (ToolName: "Search", Text: null),
            (ToolName: null, Text: "let me note that."),
            (ToolName: "Search", Text: null),
            (ToolName: null, Text: "noted again."));
        SequencedChatClient extractor = new("{}", "{}");

        RoutingChatClientFactory chatClients = new(reply);
        chatClients.Route("reply", reply);
        chatClients.Route("fill", extractor);

        var compiled = ConfigurationCompiler.Compile(
            ConfigurationLoader.LoadYaml(ProbeYaml),
            new AgentCompilationContext(chatClients) { Knowledge = port });
        var stateExtractor = CallSessionFactory.CreateExtractor(compiled, chatClients);
        VocabularyCache vocabulary = new();
        vocabulary.Replace("applies_to", ["CT900", "CT900ENT"], maxValues: 2000);
        vocabulary.Replace("brand", ["sole", "spirit"], maxValues: 2000);

        var session = new CallSessionFactory(
            compiled, new GuardEvaluator(compiled.Configuration.Guards), stateExtractor, vocabulary: vocabulary)
            .Create("call-probe-two-turn");

        await session.RunTurnAsync("what model is it", TestContext.Current.CancellationToken);
        await session.RunTurnAsync("still not sure", TestContext.Current.CancellationToken);

        // Both turns actually reached the port's narrowed-scope leg: a latch left claimed across the
        // turn boundary would have made turn two replay turn one's stored "holds nothing" instead.
        Assert.Equal(2, narrowedCalls);
    }

    /// <summary>
    /// A knowledge store that answers the full scope with nothing and throws for any narrowed one --
    /// the shape §8 step 4's own search takes once a facet is dropped.
    /// </summary>
    private sealed class ThrowingOnNarrowedScopePort : IKnowledgeRetrievalPort
    {
        private readonly Action _onNarrowedCall;

        internal ThrowingOnNarrowedScopePort(Action onNarrowedCall) => _onNarrowedCall = onNarrowedCall;

        public ValueTask<IReadOnlyList<KnowledgeCard>> SearchAsync(
            string query, CancellationToken cancellationToken = default)
        {
            if (KnowledgeScopeScope.Current?.Facets.Count == 2)
            {
                return ValueTask.FromResult<IReadOnlyList<KnowledgeCard>>([]);
            }

            _onNarrowedCall();
            throw new InvalidOperationException("the probe's own second search is down");
        }
    }

    /// <summary>
    /// A model that calls the framework's own "Search" tool (or none) on each request, in a fixed
    /// script -- <c>TextSearchProviderOptions.FunctionToolName</c>'s default, taking one required
    /// string argument named <c>userQuestion</c>, both verified against real
    /// <c>Microsoft.Agents.AI</c> 1.17.0.
    /// </summary>
    private sealed class ScriptedToolCallingChatClient : IChatClient
    {
        private readonly (string? ToolName, string? Text)[] _script;
        private int _calls;

        public ScriptedToolCallingChatClient(params (string? ToolName, string? Text)[] script) => _script = script;

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);

            var index = Interlocked.Increment(ref _calls) - 1;
            await Task.Yield();

            var (toolName, text) = _script[Math.Min(index, _script.Length - 1)];
            var responseId = Guid.NewGuid().ToString("N");

            if (toolName is not null)
            {
                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [new FunctionCallContent(
                        $"call_{index}",
                        toolName,
                        new Dictionary<string, object?>(StringComparer.Ordinal) { ["userQuestion"] = "what model is it" })])
                {
                    ResponseId = responseId,
                    MessageId = responseId,
                };
                yield break;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, text ?? string.Empty)
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
