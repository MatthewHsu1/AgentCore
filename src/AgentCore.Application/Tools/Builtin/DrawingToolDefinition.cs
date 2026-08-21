using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Tools.Drawing;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tools.Builtin;

/// <summary>The <c>ui.draw</c> definition.</summary>
internal sealed class DrawingToolDefinition : IBuiltinToolDefinition
{
    public string Name => BuiltinToolNames.Draw;

    public string DefaultDescription => "Draw something on the caller's screen for them to look at.";

    public AITool Build(ToolConfiguration tool, BuiltinToolPorts ports)
        => DrawingTool.Create(tool, ports.ChatClients);
}
