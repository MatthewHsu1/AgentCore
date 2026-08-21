using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
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
/// a boot with no <see cref="IChatClientFactory"/> bound fails naming the missing port.
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

    /// <summary>A shipped agent that needs no port of its own, so these tests isolate the one port
    /// every shipped agent needs: <see cref="IChatClientFactory"/>.</summary>
    private sealed class FakeDefinition : IShippedAgentDefinition
    {
        public const string Description = "the fake shipped agent's own default description.";

        public string Name => "fake";

        public string DefaultDescription => Description;

        public string Instructions => "be a fake shipped agent.";

        public int DefaultMaxRounds => 5;

        public IReadOnlyList<AITool> InnerTools(ToolConfiguration tool) => [];

        public string? MissingPort(BuiltinToolPorts ports) => null;
    }
}
