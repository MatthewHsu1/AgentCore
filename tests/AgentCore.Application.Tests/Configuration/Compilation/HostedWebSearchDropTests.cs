using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Tests.Runtime;
using AgentCore.Application.Tools.Builtin;
using AgentCore.Application.Tools.Registry;
using AgentCore.TestSupport;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Configuration.Compilation;

/// <summary>
/// The hosted web-search marker reaching, or not reaching, one compiled agent.
/// </summary>
public sealed class HostedWebSearchDropTests
{
    private const string SearchToolId = BuiltinToolNames.WebSearch;

    [Fact]
    public void Compile_CapableModel_KeepsTheTool()
    {
        var tools = Tools(agent: "reply", capable: true);

        Assert.Single(tools.OfType<HostedWebSearchTool>());
    }

    [Fact]
    public void Compile_IncapableModel_DropsTheTool()
    {
        var tools = Tools(agent: "reply", capable: false);

        Assert.Empty(tools.OfType<HostedWebSearchTool>());
    }

    [Fact]
    public void Compile_IncapableModel_LogsOneWarning()
    {
        var (tools, logs) = ToolsAndLogs(agent: "reply", capable: false);

        Assert.Empty(tools.OfType<HostedWebSearchTool>());
        var warning = Assert.Single(logs);
        Assert.Contains("reply", warning, StringComparison.Ordinal);
        Assert.Contains("web.search", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_IncapableModel_DoesNotThrow()
    {
        // An agent with no web search still answers. This never stops a host.
        Assert.Null(Record.Exception(() => Tools(agent: "reply", capable: false)));
    }

    [Fact]
    public void Compile_TwoAgentsOnDifferentModels_OnlyTheCapableOneKeepsIt()
    {
        var (capable, incapable) = TwoAgents();

        Assert.Single(capable.OfType<HostedWebSearchTool>());
        Assert.Empty(incapable.OfType<HostedWebSearchTool>());
    }

    [Fact]
    public void Compile_HostSuppliedHostedTool_IsDroppedByTheSameRule()
    {
        // The check tests the tool type, not the builtin name, so a hosted search tool arriving
        // from a host's own IToolSource is covered with no second place to forget.
        var tools = ToolsFromHostSource(capable: false);

        Assert.Empty(tools.OfType<HostedWebSearchTool>());
    }

    [Fact]
    public async Task Compile_TwoAgentsOnDifferentModels_ThroughTheRealCompiler_OnlyTheCapableOneKeepsIt()
    {
        // AgentToolCompiler.Build takes the model as a parameter, and the six tests above call it
        // directly. None of them exercises the expression that picks that parameter per agent
        // (ConfigurationCompiler's item.Model ?? section.Defaults?.Model), so this one goes through
        // ConfigurationCompiler.Compile on a document with two agents pinned to two different models,
        // and reads back what each agent actually sent its chat client.
        const string capableRef = "vendor-a";
        const string incapableRef = "vendor-b";

        using SequencedChatClient reply = new("hello there.");
        var registry = BuildRegistry([new BuiltinToolSource(new BuiltinToolPorts(null))], [SearchTool()]);

        var compiled = ConfigurationCompiler.Compile(
            new AgentCoreConfiguration
            {
                ApiVersion = AgentCoreConfiguration.SupportedApiVersion,
                Name = "hosted-web-search-two-models",
                Tools = [SearchTool()],
                Policy = new PolicyConfiguration
                {
                    Initial = "opening",
                    Stages =
                    [
                        new StageConfiguration { Id = "opening", Agent = "capable" },
                        new StageConfiguration { Id = "closing", Agent = "incapable" },
                    ],
                },
                Agents = new AgentsConfiguration
                {
                    Items =
                    [
                        new AgentConfiguration { Id = "capable", Model = new ModelReference { Ref = capableRef }, Tools = [SearchToolId] },
                        new AgentConfiguration { Id = "incapable", Model = new ModelReference { Ref = incapableRef }, Tools = [SearchToolId] },
                    ],
                },
            },
            new AgentCompilationContext(new RoutingCapabilityChatClientFactory(capableRef, reply)) { Tools = registry });

        var token = TestContext.Current.CancellationToken;
        await compiled.Agents["capable"].RunAsync("hi", cancellationToken: token);
        await compiled.Agents["incapable"].RunAsync("hi", cancellationToken: token);

        Assert.Single((reply.Options[0]?.Tools ?? []).OfType<HostedWebSearchTool>());
        Assert.Empty((reply.Options[1]?.Tools ?? []).OfType<HostedWebSearchTool>());
    }

    private static ToolConfiguration SearchTool()
        => new() { Id = SearchToolId, Kind = ToolKind.Builtin, Uses = BuiltinToolNames.WebSearch };

    private static List<AITool> Tools(string agent, bool capable)
        => BuildAgentTools(agent, model: null, new CapabilityChatClientFactory(capable));

    private static (List<AITool> Tools, List<string> Logs) ToolsAndLogs(string agent, bool capable)
    {
        RecordingLoggerFactory loggers = new();
        var tools = BuildAgentTools(agent, model: null, new CapabilityChatClientFactory(capable), loggers);
        return (tools, [.. loggers.Of(18).Select(line => line.Message)]);
    }

    private static (List<AITool> Capable, List<AITool> Incapable) TwoAgents()
    {
        const string capableRef = "vendor-a";
        const string incapableRef = "vendor-b";

        var factory = new RoutingCapabilityChatClientFactory(capableRef);

        var capable = BuildAgentTools("capable", new ModelReference { Ref = capableRef }, factory);
        var incapable = BuildAgentTools("incapable", new ModelReference { Ref = incapableRef }, factory);
        return (capable, incapable);
    }

    private static List<AITool> ToolsFromHostSource(bool capable)
    {
        const string hostToolId = "vendor_search";

        AgentConfiguration item = new() { Id = "reply", Tools = [hostToolId] };
        Dictionary<string, ToolConfiguration> declared = new(StringComparer.Ordinal);
        var registry = BuildRegistry([new HostSearchToolSource(hostToolId)], []);

        AgentCompilationContext context = new(new CapabilityChatClientFactory(capable)) { Tools = registry };

        return AgentToolCompiler.Build(item, model: null, declared, context, "/agents/items/0", static _ => null) ?? [];
    }

    private static List<AITool> BuildAgentTools(
        string agentId, ModelReference? model, IChatClientFactory chatClients, RecordingLoggerFactory? loggers = null)
    {
        var search = SearchTool();
        AgentConfiguration item = new() { Id = agentId, Tools = [SearchToolId] };
        Dictionary<string, ToolConfiguration> declared = new(StringComparer.Ordinal) { [SearchToolId] = search };
        var registry = BuildRegistry([new BuiltinToolSource(new BuiltinToolPorts(null))], [search]);

        AgentCompilationContext context = new(chatClients) { Tools = registry, Loggers = loggers };

        return AgentToolCompiler.Build(item, model, declared, context, "/agents/items/0", static _ => null) ?? [];
    }

    private static ToolRegistry BuildRegistry(IEnumerable<IToolSource> sources, IReadOnlyList<ToolConfiguration> declared)
    {
        AgentCoreConfiguration document = new()
        {
            ApiVersion = AgentCoreConfiguration.SupportedApiVersion,
            Name = "hosted-web-search-drop",
            Tools = declared,
        };

        return ToolRegistryBuilder.BuildAsync(sources, new ToolSourceContext(document)).AsTask().GetAwaiter().GetResult();
    }

    private sealed class HostSearchToolSource(string id) : IToolSource
    {
        public ValueTask<IReadOnlyList<ToolRegistration>> ProvideAsync(
            ToolSourceContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<ToolRegistration>>(
                [new ToolRegistration(id, "A vendor's own hosted search.", () => new HostedWebSearchTool())]);
    }

    private sealed class CapabilityChatClientFactory(bool capable) : IChatClientFactory
    {
        public IChatClient GetChatClient(ModelReference? model) => throw new NotSupportedException();

        public bool SupportsHostedWebSearch(ModelReference? model) => capable;
    }

    private sealed class RoutingCapabilityChatClientFactory(string capableRef, IChatClient? client = null) : IChatClientFactory
    {
        public IChatClient GetChatClient(ModelReference? model) => client ?? throw new NotSupportedException();

        public bool SupportsHostedWebSearch(ModelReference? model)
            => string.Equals(model?.Ref, capableRef, StringComparison.Ordinal);
    }
}
