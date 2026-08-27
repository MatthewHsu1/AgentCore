using AgentCore.TestSupport;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Knowledge.Fakes;
using AgentCore.Application.Tests.Runtime;
using Microsoft.Agents.AI;
using Xunit;

namespace AgentCore.Application.Tests.Compilation;

/// <summary>
/// Where the <c>knowledge:</c> block becomes a bound provider. The block is per agent, so the
/// binding is too: one agent in a document can retrieve and its neighbour can pay nothing for it.
/// </summary>
public sealed class KnowledgeProviderBindingTests
{
    private const string KnowledgeYaml =
        """
        apiVersion: agentcore/v1
        name: knowledge-binding
        agents:
          items:
            - { id: only, instructions: "I answer everything", knowledge: { mode: prefetch, scoped: false } }
        """;

    private const string MixedYaml =
        """
        apiVersion: agentcore/v1
        name: knowledge-binding-mixed
        policy:
          initial: greeting
          stages:
            - { id: greeting, agent: reader }
            - { id: closing, agent: quiet }
        agents:
          items:
            - { id: reader, instructions: "I read the bank", knowledge: { mode: tool, scoped: false } }
            - { id: quiet,  instructions: "I answer from my own instructions" }
        """;

    private const string NoKnowledgeYaml =
        """
        apiVersion: agentcore/v1
        name: no-knowledge-binding
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        """;

    [Fact]
    public void AnAgentWithAKnowledgeBlock_CarriesBothProviders()
    {
        var agent = CompileOne(KnowledgeYaml, new StubKnowledgePort([]));

        Assert.Contains(Providers(agent), provider => provider is TurnContextProvider);
        Assert.Contains(Providers(agent), provider => provider is TextSearchProvider);
    }

    [Fact]
    public void AnAgentWithNoKnowledgeBlock_CarriesOnlyTheTurnProvider()
    {
        var agent = CompileOne(NoKnowledgeYaml, new StubKnowledgePort([]));

        Assert.DoesNotContain(Providers(agent), provider => provider is TextSearchProvider);
    }

    [Fact]
    public void OnlyTheAgentThatDeclaredTheBlock_Retrieves()
    {
        using SequencedChatClient reply = new("hello there.");

        var compiled = ConfigurationCompiler.Compile(
            ConfigurationLoader.LoadYaml(MixedYaml),
            new AgentCompilationContext(new FakeChatClientFactory(reply))
            {
                Knowledge = new StubKnowledgePort([]),
            });

        Assert.Contains(Providers(compiled.Agents["reader"]), provider => provider is TextSearchProvider);
        Assert.DoesNotContain(Providers(compiled.Agents["quiet"]), provider => provider is TextSearchProvider);
    }

    [Fact]
    public async Task TheAgentsOwnSettings_ReachTheProviderThatWasBound()
    {
        // A compiler that composed the block and then bound something else would attach a provider
        // that works and silently ignores every key the document set.
        using SequencedChatClient reply = new("hello there.");

        var compiled = ConfigurationCompiler.Compile(
            ConfigurationLoader.LoadYaml(MixedYaml),
            new AgentCompilationContext(new FakeChatClientFactory(reply))
            {
                Knowledge = new StubKnowledgePort([]),
            });

        var reader = compiled.Agents["reader"];
        var provider = Assert.Single(Providers(reader).OfType<TextSearchProvider>());

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

    private static AIAgent CompileOne(string yaml, IKnowledgeRetrievalPort? port)
    {
        using SequencedChatClient reply = new("hello there.");

        var compiled = ConfigurationCompiler.Compile(
            ConfigurationLoader.LoadYaml(yaml),
            new AgentCompilationContext(new FakeChatClientFactory(reply)) { Knowledge = port });

        return Assert.Single(compiled.Agents.Values);
    }

    private static IEnumerable<AIContextProvider> Providers(AIAgent agent)
    {
        var inner = agent.GetService<ChatClientAgent>();
        Assert.NotNull(inner);
        return inner.AIContextProviders ?? [];
    }
}
