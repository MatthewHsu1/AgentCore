using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Tools.Builtin;
using AgentCore.Application.Tools.Shipped;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tools.Drawing;

/// <summary>
/// <c>ui.draw</c>: the agent that turns a request in words into a tree on the caller's screen.
/// </summary>
internal sealed class DrawingAgentDefinition : IShippedAgentDefinition
{
    /// <inheritdoc />
    public string Name => BuiltinToolNames.Draw;

    /// <inheritdoc />
    public string DefaultDescription
        => "Draw something on the caller's screen for them to look at. Whoever draws it cannot see "
           + "the conversation, so anything it needs has to be in the request.";

    /// <inheritdoc />
    public string Instructions => DrawingVocabulary.Text;

    /// <inheritdoc />
    public int DefaultMaxRounds => 3;

    /// <inheritdoc />
    public IReadOnlyList<AITool> InnerTools(ToolConfiguration tool, BuiltinToolPorts ports)
        => [PresentTool.Create(tool.Id)];

    /// <inheritdoc />
    public string? MissingPort(BuiltinToolPorts ports) => null;
}
