using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Tests.Runtime;
using AgentCore.Application.Tools.Builtin;
using AgentCore.Application.Tools.Shipped;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Tools;

/// <summary>
/// <see cref="ShippedAgentBuilder"/> is the one seam every shipped agent goes through to become the
/// function an outer agent calls. These tests pin the document/definition split: a shipped agent
/// cannot name its own model, only the document can override the round cap and the description, and
/// a boot with an unbound port — the one it always needs (<see cref="IChatClientFactory"/>) or one a
/// definition names of its own — fails naming both the tool id and the port.
/// </summary>
public sealed class ShippedAgentBuilderTests
{
    private static ToolConfiguration Declared(string id) => new()
    {
        Id = id,
        Kind = ToolKind.Builtin,
        Uses = id,
    };

    [Fact]
    public void Build_NoChatClientFactory_FailsTheBootNamingThePort()
    {
        var ports = new BuiltinToolPorts(null, null, null);

        var error = Assert.Throws<ConfigurationLoadException>(
            () => ShippedAgentBuilder.Build(new FakeDefinition(), Declared("draw"), ports));

        Assert.Contains("IChatClientFactory", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_DefinitionNamesAMissingPort_FailsNamingTheToolAndThePort()
    {
        var definition = new FakeDefinition(missingPort: "IKnowledgeRetrievalPort");

        var error = Assert.Throws<ConfigurationLoadException>(
            () => ShippedAgentBuilder.Build(
                definition, Declared("draw"), new BuiltinToolPorts(null, null, new RecordingChatClientFactory())));

        Assert.Contains("draw", error.Message, StringComparison.Ordinal);
        Assert.Contains("IKnowledgeRetrievalPort", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_AModelRefOnTheDeclaration_IsWhatTheFactoryIsAsked()
    {
        RecordingChatClientFactory factory = new();

        ShippedAgentBuilder.Build(
            new FakeDefinition(),
            Declared("draw") with { Model = new ModelReference { Ref = "cheap" } },
            new BuiltinToolPorts(null, null, factory));

        Assert.Equal("cheap", factory.Asked!.Ref);
    }

    [Fact]
    public void Build_NoModelRef_AsksTheFactoryForItsDefault()
    {
        RecordingChatClientFactory factory = new();

        ShippedAgentBuilder.Build(new FakeDefinition(), Declared("draw"), new BuiltinToolPorts(null, null, factory));

        Assert.Null(factory.Asked);
    }

    [Fact]
    public void Build_TheDocumentWritesNoDescription_UsesTheDefinitionsDefault()
    {
        var function = ShippedAgentBuilder.Build(
            new FakeDefinition(),
            Declared("draw") with { Description = null },
            new BuiltinToolPorts(null, null, new RecordingChatClientFactory()));

        Assert.Equal(FakeDefinition.Description, function.Description);
    }

    /// <summary>
    /// The round cap reaches the real <c>MaximumIterationsPerRequest</c> the inner loop obeys:
    /// <see cref="LoopingToolCallingChatClient"/> never answers with text, only ever a tool call, so
    /// the tool body keeps running until the cap gives up — the same measurement
    /// <c>docs/probes/ShippedAgentProbe</c> made of <c>MaximumIterationsPerRequest</c> directly. (The
    /// client's own request count runs one higher than the cap: the loop's last request comes back as
    /// one more tool call that the cap then refuses to invoke, so the tool body itself, not the
    /// request count, is what the cap actually bounds.)
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Build_DocumentMaxRounds_CapsTheInnerLoopAtThatValue(int rounds)
    {
        var calls = 0;
        var loopTool = AIFunctionFactory.Create(() => { calls++; return "keep going"; }, "loop_tool");
        var definition = new FakeDefinition(innerTools: [loopTool]);

        var function = ShippedAgentBuilder.Build(
            definition,
            Declared("draw") with { MaxRounds = rounds },
            new BuiltinToolPorts(null, null, new RecordingChatClientFactory(new LoopingToolCallingChatClient())));

        await function.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["query"] = "go" }),
            TestContext.Current.CancellationToken);

        Assert.Equal(rounds, calls);
    }

    [Fact]
    public async Task Build_NoMaxRounds_CapsTheInnerLoopAtTheDefinitionsDefault()
    {
        var calls = 0;
        var loopTool = AIFunctionFactory.Create(() => { calls++; return "keep going"; }, "loop_tool");
        var definition = new FakeDefinition(innerTools: [loopTool]);

        var function = ShippedAgentBuilder.Build(
            definition,
            Declared("draw"),
            new BuiltinToolPorts(null, null, new RecordingChatClientFactory(new LoopingToolCallingChatClient())));

        await function.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["query"] = "go" }),
            TestContext.Current.CancellationToken);

        Assert.Equal(FakeDefinition.DefaultRounds, calls);
    }

    /// <summary>A shipped agent whose missing port and inner tools a test controls, so these tests can
    /// isolate each of <see cref="ShippedAgentBuilder"/>'s two port checks and the round cap in turn.</summary>
    private sealed class FakeDefinition : IShippedAgentDefinition
    {
        public const string Description = "the fake shipped agent's own default description.";

        public const int DefaultRounds = 5;

        private readonly string? _missingPort;
        private readonly IReadOnlyList<AITool> _innerTools;

        public FakeDefinition(string? missingPort = null, IReadOnlyList<AITool>? innerTools = null)
        {
            _missingPort = missingPort;
            _innerTools = innerTools ?? [];
        }

        public string Name => "fake";

        public string DefaultDescription => Description;

        public string Instructions => "be a fake shipped agent.";

        public int DefaultMaxRounds => DefaultRounds;

        public IReadOnlyList<AITool> InnerTools(ToolConfiguration tool, BuiltinToolPorts ports) => _innerTools;

        public string? MissingPort(BuiltinToolPorts ports) => _missingPort;
    }
}
