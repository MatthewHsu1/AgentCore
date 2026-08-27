using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tools;
using AgentCore.Application.Tools.Registry;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Compilation;

/// <summary>
/// Decision 15 at the compile layer: an agent's <c>tools:</c> entry may name an id the registry
/// serves even when no <c>tools:</c> entry ever declares it — the shape an <c>mcp:</c> server's
/// discovery always takes.
/// </summary>
/// <remarks>
/// <see cref="AgentCore.TestSupport.TestToolRegistry"/> only ever serves a declared id, so every
/// other compiler test in this project builds a registry that way and never exercises this path. The
/// two tests here build the registry directly through <see cref="ToolRegistryBuilder"/> instead, so a
/// served id can be absent from <c>tools:</c> entirely.
/// </remarks>
public sealed class DiscoveredToolReferenceTests
{
    private const string DiscoveredOnlyYaml =
        """
        apiVersion: agentcore/v1
        name: discovered-only
        agents:
          items:
            - { id: only, instructions: "answer", tools: [ discovered_only ] }
        """;

    [Fact]
    public async Task AnIdNoToolsEntryDeclares_StillReachesTheModelWhenTheRegistryServesIt()
    {
        var document = ConfigurationLoader.LoadYaml(DiscoveredOnlyYaml);
        var registry = await BuildRegistryAsync(document, "discovered_only", TestContext.Current.CancellationToken);

        using ToolCallingChatClient client = new("answered");
        var compiled = ConfigurationCompiler.Compile(
            document,
            new AgentCompilationContext(new FakeChatClientFactory(client)) { Tools = registry });

        await compiled.Agent.RunAsync("hello", cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEmpty(client.Offered);
        Assert.All(client.Offered, offered => Assert.Equal("discovered_only", offered.Name));
    }

    [Fact]
    public async Task AnIdInNeitherToolsNorTheRegistry_FailsNamingTheAskingAgent()
    {
        var document = ConfigurationLoader.LoadYaml(DiscoveredOnlyYaml);
        var emptyRegistry = await BuildRegistryAsync(document, null, TestContext.Current.CancellationToken);

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationCompiler.Compile(
            document,
            new AgentCompilationContext(new FakeChatClientFactory(new ToolCallingChatClient("unused")))
            {
                Tools = emptyRegistry,
            }));

        var error = Assert.Single(failure.Errors);
        Assert.Equal("/agents/items/0/tools/0", error.Pointer);
        Assert.Contains("discovered_only", error.Message, StringComparison.Ordinal);
        Assert.Contains("is not declared in tools:, and no tool source serves it", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Builds a registry serving one id, or nothing at all when <paramref name="id"/> is <see langword="null"/>.</summary>
    private static async Task<ToolRegistry> BuildRegistryAsync(
        AgentCoreConfiguration document, string? id, CancellationToken cancellationToken)
    {
        IToolSource[] sources = id is null ? [] : [new OneToolSource(id)];
        return await ToolRegistryBuilder.BuildAsync(sources, new ToolSourceContext(document), cancellationToken);
    }

    private sealed class OneToolSource(string id) : IToolSource
    {
        public ValueTask<IReadOnlyList<ToolRegistration>> ProvideAsync(
            ToolSourceContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<ToolRegistration>>(
                [new ToolRegistration(id, "A tool discovered but never declared.", () => AIFunctionFactory.Create(() => "ok", id))]);
    }
}
