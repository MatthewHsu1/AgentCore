using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Runtime;
using AgentCore.Domain.Knowledge;
using AgentCore.TestSupport;
using Microsoft.Agents.AI;
using Xunit;

namespace AgentCore.Application.Tests.Compilation;

/// <summary>
/// Where the document's <c>providers.knowledge</c> ambiguity wiring becomes part of the agent's own
/// bound search. The wiring is document-level and the <c>knowledge:</c> block is per agent, so the
/// compiler is the only place the two meet.
/// </summary>
/// <remarks>
/// <c>KnowledgeProbeTests</c> hand-builds its <c>ResolvedKnowledge</c> and so proves only that the
/// probe reads what it is given. These tests compile a real document, so a compiler that composed
/// the block and then handed the probe an unwired one fails here — where it would otherwise ship
/// green and degrade every deployment's probe to the "holds nothing" notice.
/// </remarks>
public sealed class ClarificationWiringBindingTests
{
    private const string ModelDescription = "The model, as printed on the machine.";

    private const string WiredYaml =
        """
        apiVersion: agentcore/v1
        name: clarification-wiring
        state:
          model:
            type: string
            writer: extractor
            description: "The model, as printed on the machine."
          audience:
            type: string
            writer: const
            value: everyone
            enum: [everyone]
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
              fromState: [model, audience]
              wildcard: { value: "*", facets: [model] }
            ambiguity: { maxCandidates: 3, maxAsks: 2 }
        agents:
          items:
            - id: only
              instructions: "I read the bank"
              knowledge: { mode: tool, scoped: true }
        """;

    private const string TwoAgentYaml =
        """
        apiVersion: agentcore/v1
        name: clarification-wiring-two-agents
        state:
          model:
            type: string
            writer: extractor
            description: "The model, as printed on the machine."
          audience:
            type: string
            writer: const
            value: everyone
            enum: [everyone]
        policy:
          initial: greeting
          stages:
            - { id: greeting, agent: greeter }
            - { id: answering, agent: reader }
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
              fromState: [model, audience]
              wildcard: { value: "*", facets: [model] }
            ambiguity: { maxCandidates: 3, maxAsks: 2 }
        agents:
          items:
            - { id: greeter, instructions: "I say hello" }
            - { id: reader, instructions: "I read the bank", knowledge: { mode: tool, scoped: true } }
        """;

    [Fact]
    public void EveryAgentInOneDocument_SharesTheOneClarificationProvider()
    {
        // The provider's three fields are the document's wiring and it keeps nothing per agent, so a
        // second instance would be a second copy of the same thing. Channel 1 is bound to agents that
        // declare no knowledge: block of their own, so this is every agent in the document, not just
        // the ones that search.
        using SequencedChatClient reply = new("hello there.");

        var compiled = ConfigurationCompiler.Compile(
            ConfigurationLoader.LoadYaml(TwoAgentYaml),
            new AgentCompilationContext(new FakeChatClientFactory(reply)) { Knowledge = new ScopedFakePort() });

        var greeter = Assert.Single(Providers(compiled.Agents["greeter"]).OfType<ClarificationProvider>());
        var reader = Assert.Single(Providers(compiled.Agents["reader"]).OfType<ClarificationProvider>());

        Assert.Same(greeter, reader);
    }

    [Fact]
    public async Task TheDocumentsAmbiguityWiring_ReachesTheAgentsOwnBoundSearch()
    {
        // Every gate in the probe's own preamble reads one member of the wiring the compiler
        // carries across: drop any of ambiguity, wildcard.value, wildcard.facets, scope.template or
        // fromState on the way, and this note collapses into the bare "holds nothing" notice.
        var port = new ScopedFakePort();
        using var clarifications = TurnAmbientsTestScope.WithClarifications(new Clarifications());
        using var scope = KnowledgeScopeScope.Open(Scope(model: "*", audience: "everyone"));

        var note = await SearchAsync(CompileTheSearchProvider(port), "belt slipping");

        Assert.Contains("It could be: e33, f63", note, StringComparison.Ordinal);
        Assert.Equal(2, port.Calls);
    }

    [Fact]
    public async Task TheSlotsOwnDescription_ReachesTheNoteTheProbeWrites()
    {
        // The description lives under state:, not under providers.knowledge, so it reaches the probe
        // only because the compiler joined the two. Losing it degrades the ask to the bare slot name.
        var port = new ScopedFakePort();
        using var clarifications = TurnAmbientsTestScope.WithClarifications(new Clarifications());
        using var scope = KnowledgeScopeScope.Open(Scope(model: "*", audience: "everyone"));

        var note = await SearchAsync(CompileTheSearchProvider(port), "belt slipping");

        Assert.Contains(ModelDescription, note, StringComparison.Ordinal);
        Assert.DoesNotContain("known: model ", note, StringComparison.Ordinal);
    }

    private static TextSearchProvider CompileTheSearchProvider(IKnowledgeRetrievalPort port)
    {
        using SequencedChatClient reply = new("hello there.");

        var compiled = ConfigurationCompiler.Compile(
            ConfigurationLoader.LoadYaml(WiredYaml),
            new AgentCompilationContext(new FakeChatClientFactory(reply)) { Knowledge = port });

        return Assert.Single(Providers(compiled.Agents["only"]).OfType<TextSearchProvider>());
    }

    private static IEnumerable<AIContextProvider> Providers(AIAgent agent)
    {
        var inner = agent.GetService<ChatClientAgent>();
        Assert.NotNull(inner);

        return inner.AIContextProviders ?? [];
    }

    private static KnowledgeScope Scope(string model, string audience)
        => new()
        {
            Facets = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["model"] = model,
                ["audience"] = audience,
            },
        };

    /// <summary>The one notice the bound search returned, as text.</summary>
    private static async Task<string> SearchAsync(TextSearchProvider provider, string query)
        => Assert.Single(
            await TextSearchProviderInternals.SearchAsync(
                provider, query, TestContext.Current.CancellationToken).ConfigureAwait(false)).Text;

    /// <summary>
    /// A store that answers nothing while <c>model</c> is still in the scope, and names two models
    /// once the probe has dropped it — the shape §8 exists to resolve.
    /// </summary>
    private sealed class ScopedFakePort : IKnowledgeRetrievalPort
    {
        internal int Calls { get; private set; }

        public ValueTask<IReadOnlyList<KnowledgeCard>> SearchAsync(
            string query, CancellationToken cancellationToken = default)
        {
            Calls++;

            var facets = KnowledgeScopeScope.Current?.Facets;
            if (facets is null || facets.ContainsKey("model"))
            {
                return new([]);
            }

            return new([Card("a", "e33"), Card("b", "f63")]);
        }

        private static KnowledgeCard Card(string id, string model)
            => new()
            {
                CardId = id,
                Text = "card " + id,
                ViaLink = false,
                Extras = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["facets"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["model"] = model },
                },
            };
    }
}
