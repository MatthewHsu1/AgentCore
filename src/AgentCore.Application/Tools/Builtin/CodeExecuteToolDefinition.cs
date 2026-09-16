using AgentCore.Application.Configuration.Schema;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tools.Builtin;

/// <summary>
/// The <c>uses: code.execute</c> builtin: one <see cref="HostedCodeInterpreterTool"/>.
/// </summary>
internal sealed class CodeExecuteToolDefinition : IBuiltinToolDefinition
{
    /// <inheritdoc />
    public string Name => BuiltinToolNames.CodeExecute;

    /// <inheritdoc />
    public string DefaultDescription =>
        "Writes and runs Python code in the model provider's hosted sandbox.";

    /// <inheritdoc />
    public AITool Build(ToolConfiguration tool, BuiltinToolPorts ports) => new HostedCodeInterpreterTool();
}
