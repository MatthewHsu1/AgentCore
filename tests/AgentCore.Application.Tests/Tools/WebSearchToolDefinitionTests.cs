using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Tools.Builtin;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Tools;

/// <summary>The <c>uses: web.search</c> builtin, which builds a marker and nothing else.</summary>
public sealed class WebSearchToolDefinitionTests
{
    [Fact]
    public void Name_IsTheDocumentedUsesValue()
    {
        Assert.Equal("web.search", BuiltinToolNames.WebSearch);
    }

    [Fact]
    public void Build_ProducesAHostedWebSearchTool()
    {
        var tool = new WebSearchToolDefinition().Build(
            new ToolConfiguration { Id = "search", Kind = ToolKind.Builtin, Uses = BuiltinToolNames.WebSearch },
            new BuiltinToolPorts(ChatClients: null));

        Assert.IsType<HostedWebSearchTool>(tool);
    }

    [Fact]
    public void Build_NeedsNoPorts()
    {
        // The marker carries no behaviour, so an unbound chat client factory is not a failure here.
        var exception = Record.Exception(() => new WebSearchToolDefinition().Build(
            new ToolConfiguration { Id = "search", Kind = ToolKind.Builtin, Uses = BuiltinToolNames.WebSearch },
            new BuiltinToolPorts(ChatClients: null)));

        Assert.Null(exception);
    }
}
