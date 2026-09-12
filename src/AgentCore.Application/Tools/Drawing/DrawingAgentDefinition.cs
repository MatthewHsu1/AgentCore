using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
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
        => "Draw something on the caller's screen for them to look at. To draw what a tool answered, "
           + "name the tool and say what to show, e.g. 'a table of the lookup_orders result with "
           + "columns order, customer, total'; never copy the rows into the request. Whoever draws "
           + "it cannot see the conversation, so anything else it needs has to be in the request.";

    /// <inheritdoc />
    public string Instructions => DrawingVocabulary.Text;

    /// <inheritdoc />
    public int DefaultMaxRounds => 3;

    /// <inheritdoc />
    public IReadOnlyList<AITool> InnerTools(ToolConfiguration tool, BuiltinToolPorts ports)
        => [PresentTool.Create(tool.Id, ports.Scripts!)];

    /// <inheritdoc />
    public string? MissingPort(BuiltinToolPorts ports)
        => ports.Scripts is null ? nameof(IScriptRunnerPort) : null;

    /// <inheritdoc />
    /// <remarks>
    /// The drawing model cannot see the conversation, and it must not see the rows either: they go
    /// to its script. What it gets is the request and, after it, the shape of each answer.
    /// </remarks>
    public string Compose(string query)
        => DrawingData.Describe(TurnAmbients.Current?.Results) is { } data
            ? query + "\n\n" + data
            : query;
}
