using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Tools.Fakes;
using AgentCore.Application.Tools;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Tools;

/// <summary>
/// One <see cref="IAgentToolFactory"/> over the three kinds that need one.
/// </summary>
/// <remarks>
/// The compile table asks one factory for every declared tool. This one holds a link for each kind
/// and asks them in order, so a host adds a kind by adding a link. <c>kind: agent</c> never reaches
/// a link, because the compile table already builds it.
/// </remarks>
public sealed class CompositeAgentToolFactoryTests
{
    private const string BuiltinAndBindingYaml =
        """
        apiVersion: agentcore/v1
        name: mixed
        tools:
          - { id: search_chunks, kind: builtin, uses: knowledge.search }
          - id: create_case
            kind: binding
            binds: CreateCase
            description: Open a service case.
            parameters:
              type: object
              properties: { summary: { type: string } }
              required: [ summary ]
        agents:
          items:
            - { id: front, instructions: "answer", tools: [ search_chunks, create_case ] }
        policy:
          initial: talk
          stages:
            - { id: talk, agent: front, terminal: true }
        """;

    [Fact]
    public void AKindAgentTool_TakesNoLinkAndReturnsNull()
    {
        // The interface says a factory answers null for a kind it does not serve, and the compile
        // table builds this kind itself through AsAIFunction().
        var factory = Factory();

        Assert.Null(factory.Create(new ToolConfiguration { Id = "ask", Kind = ToolKind.Agent, Agent = "inner" }));
    }

    [Fact]
    public void ItDispatchesByKind()
    {
        var factory = Factory();

        var builtin = factory.Create(new ToolConfiguration
        {
            Id = "search_chunks",
            Kind = ToolKind.Builtin,
            Uses = BuiltinToolNames.KnowledgeSearch,
        });

        var binding = factory.Create(new ToolConfiguration
        {
            Id = "create_case",
            Kind = ToolKind.Binding,
            Binds = "CreateCase",
        });

        Assert.Equal("search_chunks", Assert.IsAssignableFrom<AIFunction>(builtin).Name);
        Assert.Equal("create_case", Assert.IsAssignableFrom<AIFunction>(binding).Name);
    }

    [Fact]
    public void AKindNoLinkServes_FailsAtStartupInsteadOfDisappearing()
    {
        // The compile table drops a null quietly, which is right for kind: agent and wrong for
        // everything else. A tool the document declares and the agent lists has to exist.
        CompositeAgentToolFactory factory = new([]);

        var failure = Assert.Throws<ConfigurationLoadException>(() => factory.Create(new ToolConfiguration
        {
            Id = "lookup_order",
            Kind = ToolKind.Http,
            Request = new HttpRequestConfiguration { Method = "GET", Url = "https://api.example.com" },
        }));

        Assert.Contains("lookup_order", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCompileTable_AdvertisesBothToolsToTheModel()
    {
        using ToolCallingChatClient client = new("done");
        var document = ConfigurationLoader.LoadYaml(BuiltinAndBindingYaml);

        var compiled = ConfigurationCompiler.Compile(
            document,
            new AgentCompilationContext(new FakeChatClientFactory(client)) { Tools = Factory() });

        await compiled.Agent.RunAsync("help me", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["search_chunks", "create_case"], client.Offered.Take(2).Select(function => function.Name));
    }

    private static CompositeAgentToolFactory Factory()
    {
        ToolBindingRegistry registry = new();
        registry.Register("CreateCase", (arguments, cancellationToken) => ValueTask.FromResult<object?>(null));

        return new CompositeAgentToolFactory(
        [
            new BuiltinToolFactory(new MapKnowledgePort(), new MapKnowledgePort()),
            new BindingToolFactory(registry),
        ]);
    }
}
