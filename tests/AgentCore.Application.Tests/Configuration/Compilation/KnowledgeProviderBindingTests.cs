using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Knowledge;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Knowledge.Fakes;
using AgentCore.Application.Tests.Runtime;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Configuration.Compilation;

/// <summary>
/// Where the <c>knowledge:</c> block becomes a bound provider. The block is per agent, so the
/// binding is too: one agent in a document can retrieve and its neighbour can pay nothing for it.
/// </summary>
public sealed class KnowledgeProviderBindingTests
{
    private const string KnowledgeYaml =
        """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "I answer everything", knowledge: { mode: prefetch, scoped: false } }
        entries:
          main:
            agent: only
        """;

    private const string MixedYaml =
        """
          apiVersion: agentcore/v1
          agents:
            items:
              - { id: reader, instructions: "I read the bank", knowledge: { mode: tool, scoped: false } }
              - { id: quiet,  instructions: "I answer from my own instructions" }
          entries:
            main:
              policy:
                initial: greeting
                stages:
                  - { id: greeting, agent: reader }
                  - { id: closing, agent: quiet }
          """;

      private const string FilterableYaml =
          """
        apiVersion: agentcore/v1
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
              filterable:
                - key: model
                  description: "The machine and the year, as one tag, such as lcr-2023."
        agents:
          items:
            - { id: only, instructions: "I read the bank", knowledge: { mode: tool, scoped: false } }
        entries:
          main:
            agent: only
        """;

    private const string NoKnowledgeYaml =
        """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        entries:
          main:
            agent: only
        """;

    [Fact]
    public void AnAgentWithAKnowledgeBlock_CarriesBothProviders()
    {
        var agent = CompileOne(KnowledgeYaml, new StubKnowledgePort([]));

        Assert.Contains(Providers(agent), provider => provider is TurnContextProvider);
        Assert.Contains(Providers(agent), provider => provider is KnowledgePrefetchProvider);
    }

    [Fact]
    public void AnAgentWithNoKnowledgeBlock_CarriesOnlyTheTurnProvider()
    {
        var agent = CompileOne(NoKnowledgeYaml, new StubKnowledgePort([]));

        Assert.DoesNotContain(Providers(agent), provider => provider is FacetFilterProvider or KnowledgePrefetchProvider);
    }

    [Fact]
    public void OnlyTheAgentThatDeclaredTheBlock_Retrieves()
    {
        using SequencedChatClient reply = new("hello there.");

        var compiled = ConfigurationCompiler.CompileAll(
            ConfigurationLoader.LoadYaml(MixedYaml),
            new AgentCompilationContext(new FakeChatClientFactory(reply))
            {
                Knowledge = new StubKnowledgePort([]),
            })["main"];

        Assert.Contains(Providers(compiled.Agents["reader"]), provider => provider is FacetFilterProvider);
        Assert.DoesNotContain(Providers(compiled.Agents["quiet"]), provider => provider is FacetFilterProvider or KnowledgePrefetchProvider);
    }

    [Fact]
    public async Task TheAgentsOwnSettings_ReachTheProviderThatWasBound()
    {
        // A compiler that composed the block and then bound something else would attach a provider
        // that works and silently ignores every key the document set.
        using SequencedChatClient reply = new("hello there.");

        var compiled = ConfigurationCompiler.CompileAll(
            ConfigurationLoader.LoadYaml(MixedYaml),
            new AgentCompilationContext(new FakeChatClientFactory(reply))
            {
                Knowledge = new StubKnowledgePort([]),
            })["main"];

        var reader = compiled.Agents["reader"];
        var provider = Assert.Single(Providers(reader).OfType<FacetFilterProvider>());

#pragma warning disable MAAI001 // The context constructors are the framework's own experimental surface.
        AIContextProvider.InvokingContext context = new(reader, null, new AIContext());
#pragma warning restore MAAI001
        var result = await provider.InvokingAsync(context, TestContext.Current.CancellationToken);

        // mode: tool, from the document. Anything else would have prefetched instead.
        Assert.NotNull(result.Tools);
        Assert.Single(result.Tools);
    }

    [Fact]
    public void AKnowledgeBlockOverAHostThatBoundNoPort_FailsTheStart()
    {
        // Ruling 17a. Compiling this to an agent with no provider is the silent fail-open the A16
        // notice exists to eliminate: it answers from its own weights and says so to nobody. The
        // failure names the agent and the missing seam, so the deployer knows which half to fix.
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => CompileOne(KnowledgeYaml, port: null));

        Assert.Contains("agent 'only'", failure.Message, StringComparison.Ordinal);
        Assert.Contains("no knowledge vendor", failure.Message, StringComparison.Ordinal);
        Assert.Equal("/agents/items/0/knowledge", Assert.Single(failure.Errors).Pointer);
    }

    [Fact]
    public async Task AFilterableFacet_ReachesTheSearchToolTheAgentIsGiven()
    {
        // The declaration crosses four layers before the model sees it: the document schema has to
        // allow the block, the record has to bind it, the compiler has to carry it to the factory,
        // and the factory has to wrap the function. Every one of those is silent when it drops it,
        // and the agent then searches the whole corpus and nobody is told.
        var agent = CompileOne(FilterableYaml, new StubKnowledgePort([]));

        List<AIFunction> offered = [];
        foreach (var provider in Providers(agent))
        {
#pragma warning disable MAAI001 // The context constructors are the framework's own experimental surface.
            AIContextProvider.InvokingContext context = new(agent, null, new AIContext());
#pragma warning restore MAAI001
            var result = await provider.InvokingAsync(context, TestContext.Current.CancellationToken);
            offered.AddRange((result.Tools ?? []).OfType<AIFunction>());
        }

        var tool = Assert.Single(offered);
        var keys = tool.JsonSchema
            .GetProperty("properties").GetProperty("filters")
            .GetProperty("items").GetProperty("properties").GetProperty("key")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(value => value.GetString());

        Assert.Equal(["model"], keys);
    }

    private static AIAgent CompileOne(string yaml, IKnowledgeRetrievalPort? port)
    {
        using SequencedChatClient reply = new("hello there.");

        var compiled = ConfigurationCompiler.CompileAll(
            ConfigurationLoader.LoadYaml(yaml),
            new AgentCompilationContext(new FakeChatClientFactory(reply)) { Knowledge = port })["main"];

        return Assert.Single(compiled.Agents.Values);
    }

    private static IEnumerable<AIContextProvider> Providers(AIAgent agent)
    {
        var inner = agent.GetService<ChatClientAgent>();
        Assert.NotNull(inner);
        return inner.AIContextProviders ?? [];
    }
}
