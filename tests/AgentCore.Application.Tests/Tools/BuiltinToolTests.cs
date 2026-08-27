using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Tests.Configuration;
using AgentCore.Application.Tools.Registry;
using AgentCore.Application.Tools;
using AgentCore.Application.Tools.Builtin;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Tools;

/// <summary>
/// The first tool kind of section 8.1: <c>kind: builtin</c>, which AgentCore ships.
/// </summary>
/// <remarks>
/// The shipped example's only built-in today is <c>ui.draw</c>, a shipped agent built through
/// <see cref="ShippedAgentBuilder"/> and tested in <c>ShippedAgentBuilderTests</c>. AgentCore ships
/// no plain-function built-in right now, so the call path, the section 8.7 error-result shape,
/// description resolution, and cancellation that a plain built-in exercises are untested until one
/// ships again.
/// </remarks>
public sealed class BuiltinToolTests
{
    // ---------------------------------------------------------------------------------------------
    // Binding the name.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void TheWorkedExample_BindsEveryBuiltInName()
    {
        var document = ConfigurationLoader.LoadYaml(ExampleDocument.Yaml);
        var factory = Factory();

        foreach (var tool in document.Tools.Where(tool => tool.Kind == ToolKind.Builtin))
        {
            Assert.NotNull(factory.Create(tool));
        }
    }

    [Fact]
    public void AUsesNameNobodyShips_FailsAtStartup()
    {
        var tool = new ToolConfiguration { Id = "x", Kind = ToolKind.Builtin, Uses = "knowledge.summarise" };

        var failure = Assert.Throws<ConfigurationLoadException>(() => Factory().Create(tool));

        Assert.Equal(ConfigurationCheck.ReferenceResolution, failure.Check);
        Assert.Contains("knowledge.summarise", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBuiltinFactory_ServesNoOtherKind()
    {
        Assert.Null(Factory().Create(new ToolConfiguration { Id = "other", Kind = ToolKind.Binding, Binds = "X" }));
        Assert.Null(Factory().Create(new ToolConfiguration { Id = "inner", Kind = ToolKind.Agent, Agent = "a" }));
    }

    [Fact]
    public async Task AUsesNameAgentCoreDoesNotShip_FailsTheBoot()
    {
        BuiltinToolSource source = new(new BuiltinToolPorts(null));
        var context = new ToolSourceContext(new AgentCoreConfiguration
        {
            ApiVersion = "agentcore/v1",
            Name = "test",
            Tools = [new ToolConfiguration { Id = "x", Kind = ToolKind.Builtin, Uses = "knowledge.invent" }],
        });

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(async () =>
            await source.ProvideAsync(context, TestContext.Current.CancellationToken));

        Assert.Contains("knowledge.invent", failure.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------
    private static BuiltinFactory Factory() => new();

    /// <summary>Builds one declared tool through <see cref="BuiltinToolSource"/>, synchronously.</summary>
    private sealed class BuiltinFactory
    {
        // The chat client factory is always bound: ui.draw is a shipped agent, and one declared in
        // a document with no factory behind it fails the boot rather than building.
        private readonly BuiltinToolSource _source =
            new(new BuiltinToolPorts(new RecordingChatClientFactory()));

        public AITool? Create(ToolConfiguration tool)
        {
            if (tool.Kind != ToolKind.Builtin)
            {
                return null;
            }

            var context = new ToolSourceContext(new AgentCoreConfiguration
            {
                ApiVersion = "agentcore/v1",
                Name = "test",
                Tools = [tool],
            });

            var registrations = _source.ProvideAsync(context).AsTask().GetAwaiter().GetResult();
            return Assert.Single(registrations).Materialise();
        }
    }
}
