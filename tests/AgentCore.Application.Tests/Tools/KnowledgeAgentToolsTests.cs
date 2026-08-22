using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Tests.Tools.Fakes;
using AgentCore.Application.Tools.Builtin;
using AgentCore.Application.Tools.Shipped;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Tools;

/// <summary>
/// The agentic search agent calls the same four knowledge built-ins the document can declare, but
/// at fixed inner names of its own. These tests hold that the names are stable — the instructions
/// name them in prose and nothing else checks the two agree — and that a missing port is still the
/// boot failure it is for a declared tool.
/// </summary>
public sealed class KnowledgeAgentToolsTests
{
    private static readonly string[] ExpectedInnerNames = ["search", "read", "list", "grep"];

    private static BuiltinToolPorts Bound(MapKnowledgePort port) => new(port, port, null);

    [Fact]
    public void Build_BothPortsBound_ProducesTheFourToolsAtTheirInnerNames()
    {
        var tools = KnowledgeAgentTools.Build(Bound(new MapKnowledgePort()));

        Assert.Equal(
            ExpectedInnerNames,
            tools.OfType<AIFunction>().Select(tool => tool.Name).ToArray());
    }

    [Fact]
    public void Build_BothPortsBound_EveryToolCarriesADescription()
    {
        var tools = KnowledgeAgentTools.Build(Bound(new MapKnowledgePort()));

        Assert.All(
            tools.OfType<AIFunction>(),
            tool => Assert.False(string.IsNullOrWhiteSpace(tool.Description)));
    }

    [Fact]
    public void Names_MatchTheToolsBuild_Produces()
    {
        var tools = KnowledgeAgentTools.Build(Bound(new MapKnowledgePort()));

        Assert.Equal(KnowledgeAgentTools.Names, tools.OfType<AIFunction>().Select(tool => tool.Name).ToArray());
    }

    [Fact]
    public void Build_NoRetrievalPort_FailsNamingTheInnerToolAndThePort()
    {
        var port = new MapKnowledgePort();

        var error = Assert.Throws<ConfigurationLoadException>(
            () => KnowledgeAgentTools.Build(new BuiltinToolPorts(null, port, null)));

        Assert.Contains("search", error.Message, StringComparison.Ordinal);
        Assert.Contains("IKnowledgeRetrievalPort", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_NoDocumentStorePort_FailsNamingTheInnerToolAndThePort()
    {
        var port = new MapKnowledgePort();

        var error = Assert.Throws<ConfigurationLoadException>(
            () => KnowledgeAgentTools.Build(new BuiltinToolPorts(port, null, null)));

        Assert.Contains("IDocumentStorePort", error.Message, StringComparison.Ordinal);
    }
}
