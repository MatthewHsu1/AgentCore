using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Tools.Builtin;
using AgentCore.Application.Tools.Drawing;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tools.Shipped;

/// <summary>
/// <c>ui.draw</c>: the agent that turns a request in words into a tree on the caller's screen.
/// </summary>
/// <remarks>
/// The vocabulary of things that can be drawn is this agent's instructions and never reaches the
/// outer agent, which sees one string parameter and nothing else. A tree that does not validate
/// comes back to this agent as <see cref="PresentTool"/>'s error result, and its own tool loop is
/// what tries again.
/// </remarks>
internal sealed class DrawingAgentDefinition : IShippedAgentDefinition
{
    /// <inheritdoc />
    public string Name => BuiltinToolNames.Draw;

    /// <inheritdoc />
    /// <remarks>
    /// The second sentence is the only place it can be said. <c>kind: builtin</c> forbids
    /// <c>parameters:</c> (schema <c>$defs/tool.allOf[0]</c>), and the one argument
    /// <c>AsAIFunction()</c> generates carries the framework's own wording, so a document has no
    /// lever on it. Without the clause a terse request draws a chart with no data in it.
    /// </remarks>
    public string DefaultDescription
        => "Draw something on the caller's screen for them to look at. Whoever draws it cannot see "
           + "the conversation, so anything it needs has to be in the request.";

    /// <inheritdoc />
    public string Instructions => DrawingVocabulary.Text;

    /// <inheritdoc />
    /// <remarks>
    /// Section 8.7 budgets 40 rounds for the calling agent and this whole tool is one of them, so a
    /// drawing model that cannot produce a valid tree in three tries is not going to.
    /// </remarks>
    public int DefaultMaxRounds => 3;

    /// <inheritdoc />
    public IReadOnlyList<AITool> InnerTools(ToolConfiguration tool, BuiltinToolPorts ports)
        => [PresentTool.Create(tool.Id)];

    /// <inheritdoc />
    /// <remarks>
    /// It reads no knowledge port. Its one requirement is a chat client factory, and
    /// <see cref="ShippedAgentBuilder"/> checks that for every shipped agent.
    /// </remarks>
    public string? MissingPort(BuiltinToolPorts ports) => null;
}
