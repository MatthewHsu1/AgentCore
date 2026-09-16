using System.Text.Json;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Knowledge;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Runtime;
using AgentCore.Domain.Knowledge;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Configuration.Compilation;

/// <summary>
/// Where the document's <c>providers.knowledge</c> ambiguity wiring becomes part of the agent's own
/// bound search. The wiring is document-level and the <c>knowledge:</c> block is per agent, so the
/// compiler is the only place the two meet.
/// </summary>
public sealed class ClarificationWiringBindingTests
{
    private const string ModelDescription = "The model, as printed on the machine.";

    private const string WiredYaml =
        """
        apiVersion: agentcore/v1
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
        entries:
          main:
            agent: only
        """;

    [Fact]
    public async Task TheDocumentsAmbiguityWiring_ReachesTheAgentsOwnBoundSearch()
    {
        // Every gate in the probe's own preamble reads one member of the wiring the compiler
        // carries across: drop any of ambiguity, wildcard.value, wildcard.facets, scope.template or
        // fromState on the way, and this note collapses into the bare "holds nothing" notice.
        var port = new ScopedFakePort();
        var turn = new TurnInvocation
        {
            CallId = "call",
            TurnIndex = 0,
            Stage = "",
            Knowledge = Scope(model: "*", audience: "everyone"),
            Clarifications = new Clarifications(),
        };

        var note = await SearchAsync(CompileTheSearchProvider(port), "belt slipping", turn);

        Assert.Contains("It could be: e33, f63", note, StringComparison.Ordinal);
        Assert.Equal(2, port.Calls);
    }

    [Fact]
    public async Task TheSlotsOwnDescription_ReachesTheNoteTheProbeWrites()
    {
        // The description lives under state:, not under providers.knowledge, so it reaches the probe
        // only because the compiler joined the two. Losing it degrades the ask to the bare slot name.
        var port = new ScopedFakePort();
        var turn = new TurnInvocation
        {
            CallId = "call",
            TurnIndex = 0,
            Stage = "",
            Knowledge = Scope(model: "*", audience: "everyone"),
            Clarifications = new Clarifications(),
        };

        var note = await SearchAsync(CompileTheSearchProvider(port), "belt slipping", turn);

        Assert.Contains(ModelDescription, note, StringComparison.Ordinal);
        Assert.DoesNotContain("known: model ", note, StringComparison.Ordinal);
    }

    private static FacetFilterProvider CompileTheSearchProvider(IKnowledgeRetrievalPort port)
    {
        using SequencedChatClient reply = new("hello there.");

        var compiled = ConfigurationCompiler.CompileAll(
            ConfigurationLoader.LoadYaml(WiredYaml),
            new AgentCompilationContext(new FakeChatClientFactory(reply)) { Knowledge = port })["main"];

        return Assert.Single(Providers(compiled.Agents["only"]).OfType<FacetFilterProvider>());
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
    private static async Task<string> SearchAsync(AIContextProvider provider, string query, TurnInvocation turn)
    {
        StubSession session = new();
        var context = await provider.InvokingAsync(
            Invoking("hello", session), TestContext.Current.CancellationToken).ConfigureAwait(false);
        var search = Assert.Single(context.Tools!, tool => tool.Name == "Search");
        var results = await ((AIFunction)search).InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["userQuestion"] = query,
                [TurnInvocation.ArgumentsKey] = turn,
            }),
            TestContext.Current.CancellationToken).ConfigureAwait(false)
            as IReadOnlyList<TextSearchProvider.TextSearchResult>;
        return Assert.Single(results!).Text;
    }

    /// <summary>Runs the provider the way the framework runs it, over one caller message.</summary>
    private static AIContextProvider.InvokingContext Invoking(string text, AgentSession session)
    {
#pragma warning disable MAAI001 // The context constructors are the framework's own experimental surface.
        return new(
            StubAgent.Instance,
            session,
            new AIContext { Messages = [new ChatMessage(ChatRole.User, text)] });
#pragma warning restore MAAI001
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

    /// <summary>
    /// A store that answers nothing while <c>model</c> is still in the scope, and names two models
    /// once the probe has dropped it — the shape §8 exists to resolve.
    /// </summary>
    private sealed class ScopedFakePort : IKnowledgeRetrievalPort
    {
        internal int Calls { get; private set; }

        public ValueTask<IReadOnlyList<KnowledgeCard>> SearchAsync(
            string query, KnowledgeScope? scope = null, CancellationToken cancellationToken = default)
        {
            Calls++;

            var facets = scope?.Facets;
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
