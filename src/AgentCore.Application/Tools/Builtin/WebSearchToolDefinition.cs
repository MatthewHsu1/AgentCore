using AgentCore.Application.Configuration.Schema;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tools.Builtin;

/// <summary>
/// The <c>uses: web.search</c> builtin: one <see cref="HostedWebSearchTool"/>.
/// </summary>
internal sealed class WebSearchToolDefinition : IBuiltinToolDefinition
{
    /// <inheritdoc />
    public string Name => BuiltinToolNames.WebSearch;

    /// <inheritdoc />
    public string DefaultDescription =>
        "Searches the public web and reads the pages it finds.";

    /// <inheritdoc />
    public AITool Build(ToolConfiguration tool, BuiltinToolPorts ports) => new HostedWebSearchTool();
}
