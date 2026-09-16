using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Tools.Builtin;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Tools;

/// <summary>The <c>uses: code.execute</c> builtin, which builds a marker and nothing else.</summary>
public sealed class CodeExecuteToolDefinitionTests
{
    [Fact]
    public void Name_IsTheDocumentedUsesValue()
    {
        Assert.Equal("code.execute", BuiltinToolNames.CodeExecute);
    }

    [Fact]
    public void Build_ProducesAHostedCodeInterpreterTool()
    {
        var tool = new CodeExecuteToolDefinition().Build(
            new ToolConfiguration { Id = "python", Kind = ToolKind.Builtin, Uses = BuiltinToolNames.CodeExecute },
            new BuiltinToolPorts(ChatClients: null));

        Assert.IsType<HostedCodeInterpreterTool>(tool);
    }

    [Fact]
    public void Build_NeedsNoPorts()
    {
        // The marker carries no behaviour, so an unbound chat client factory is not a failure here.
        var exception = Record.Exception(() => new CodeExecuteToolDefinition().Build(
            new ToolConfiguration { Id = "python", Kind = ToolKind.Builtin, Uses = BuiltinToolNames.CodeExecute },
            new BuiltinToolPorts(ChatClients: null)));

        Assert.Null(exception);
    }
}
