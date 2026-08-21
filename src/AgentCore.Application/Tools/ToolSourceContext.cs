using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.Tools;

/// <summary>
/// What a tool source is handed when it is asked what it serves.
/// </summary>
/// <param name="Configuration">The loaded document. A source reads the declarations that are its own.</param>
public sealed record ToolSourceContext(AgentCoreConfiguration Configuration)
{
    /// <summary>Every declaration of one kind, in document order.</summary>
    /// <param name="kind">The kind this source serves.</param>
    /// <returns>The declarations. A source that serves a kind nothing declares gets none.</returns>
    public IEnumerable<ToolConfiguration> DeclarationsOf(ToolKind kind)
        => Configuration.Tools.Where(tool => tool.Kind == kind);
}
