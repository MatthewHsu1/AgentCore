using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Tools;

/// <summary>
/// The map from a tool id to the tool, and the rules the boot enforces on it.
/// </summary>
/// <remarks>
/// Sources push their tools in at startup. The id and the description are known then, so the boot
/// can reject them. The tool itself is built on the first resolve, which is what lets a kind: agent
/// tool be registered before the agent it delegates to has compiled.
/// </remarks>
public sealed class ToolRegistryTests
{
    private const string ToolId = "search_chunks";
    private const string OtherId = "create_case";
    private const string Description = "Find a passage.";

    [Fact]
    public async Task ARegisteredTool_ResolvesByItsId()
    {
        var context = ContextFor(ToolId);

        var registry = await ToolRegistryBuilder.BuildAsync([SourceOf(ToolId)], context, TestContext.Current.CancellationToken);

        Assert.Equal(ToolId, registry.Resolve(ToolId).Name);
    }

    [Fact]
    public async Task ATool_IsNotBuiltUntilItIsResolved()
    {
        var built = 0;
        FakeToolSource source = new([new ToolRegistration(ToolId, Description, () => { built++; return Stub(ToolId); })]);

        var registry = await ToolRegistryBuilder.BuildAsync([source], ContextFor(ToolId), TestContext.Current.CancellationToken);

        Assert.Equal(0, built);
        registry.Resolve(ToolId);
        Assert.Equal(1, built);
    }

    [Fact]
    public async Task AResolvedTool_IsBuiltOnlyOnce()
    {
        var built = 0;
        FakeToolSource source = new([new ToolRegistration(ToolId, Description, () => { built++; return Stub(ToolId); })]);
        var registry = await ToolRegistryBuilder.BuildAsync([source], ContextFor(ToolId), TestContext.Current.CancellationToken);

        registry.Resolve(ToolId);
        registry.Resolve(ToolId);

        Assert.Equal(1, built);
    }

    [Fact]
    public async Task TwoSourcesClaimingOneId_FailTheBoot()
    {
        var context = ContextFor(ToolId);

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(async () =>
            await ToolRegistryBuilder.BuildAsync(
                [SourceOf(ToolId), SourceOf(ToolId)], context, TestContext.Current.CancellationToken));

        Assert.Contains(ToolId, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeclaredToolNoSourceClaims_FailsTheBoot()
    {
        var context = ContextFor(ToolId, OtherId);

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(async () =>
            await ToolRegistryBuilder.BuildAsync([SourceOf(ToolId)], context, TestContext.Current.CancellationToken));

        Assert.Contains(OtherId, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownId_ThrowsOnResolve()
    {
        var registry = await ToolRegistryBuilder.BuildAsync(
            [SourceOf(ToolId)], ContextFor(ToolId), TestContext.Current.CancellationToken);

        Assert.Throws<KeyNotFoundException>(() => registry.Resolve(OtherId));
    }

    [Fact]
    public async Task ADeclaredAgentTool_BuildsWithNoSourceServingIt()
    {
        var context = ContextFor(ToolKind.Agent, "delegate");

        var registry = await ToolRegistryBuilder.BuildAsync([], context, TestContext.Current.CancellationToken);

        Assert.False(registry.Contains("delegate"));
    }

    [Fact]
    public async Task AToolWithNoDescription_FailsTheBoot()
    {
        FakeToolSource source = new([new ToolRegistration(ToolId, string.Empty, () => Stub(ToolId))]);

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(async () =>
            await ToolRegistryBuilder.BuildAsync([source], ContextFor(ToolId), TestContext.Current.CancellationToken));

        Assert.Contains(ToolId, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AToolWithAWhitespaceDescription_FailsTheBoot()
    {
        FakeToolSource source = new([new ToolRegistration(ToolId, "   ", () => Stub(ToolId))]);

        await Assert.ThrowsAsync<ConfigurationLoadException>(async () =>
            await ToolRegistryBuilder.BuildAsync([source], ContextFor(ToolId), TestContext.Current.CancellationToken));
    }

    private static ToolSourceContext ContextFor(params string[] ids)
        => ContextFor(ToolKind.Binding, ids);

    private static ToolSourceContext ContextFor(ToolKind kind, params string[] ids)
    {
        List<ToolConfiguration> tools = [];
        foreach (var id in ids)
        {
            tools.Add(kind == ToolKind.Agent
                ? new ToolConfiguration { Id = id, Kind = ToolKind.Agent, Agent = "inner" }
                : new ToolConfiguration { Id = id, Kind = ToolKind.Binding, Binds = id });
        }

        return new ToolSourceContext(new AgentCoreConfiguration { ApiVersion = "agentcore/v1", Name = "test", Tools = tools });
    }

    private static FakeToolSource SourceOf(string id)
        => new([new ToolRegistration(id, Description, () => Stub(id))]);

    private static AIFunction Stub(string id)
        => AIFunctionFactory.Create(() => "ok", new AIFunctionFactoryOptions { Name = id, Description = Description });

    private sealed class FakeToolSource(IReadOnlyList<ToolRegistration> registrations) : IToolSource
    {
        public ValueTask<IReadOnlyList<ToolRegistration>> ProvideAsync(
            ToolSourceContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(registrations);
    }
}
