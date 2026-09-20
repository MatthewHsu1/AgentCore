using AgentCore.Application.Configuration.Schema;
using Microsoft.Extensions.Caching.Hybrid;

namespace AgentCore.Application.Tools.Registry;

/// <summary>
/// What a tool source is handed when it is asked what it serves.
/// </summary>
/// <param name="Configuration">The loaded document. A source reads the declarations that are its own.</param>
public sealed record ToolSourceContext(AgentCoreConfiguration Configuration)
{
    /// <summary>
    /// Gets the cache a <c>cacheSeconds:</c> tool reads through, or <see langword="null"/> when
    /// the host set none, in which case every call reaches the tool.
    /// </summary>
    public HybridCache? Cache { get; init; }

    /// <summary>Every declaration of one kind, in document order.</summary>
    /// <param name="kind">The kind this source serves.</param>
    /// <returns>The declarations. A source that serves a kind nothing declares gets none.</returns>
    public IEnumerable<ToolConfiguration> DeclarationsOf(ToolKind kind)
        => Configuration.Tools.Where(tool => tool.Kind == kind);
}
