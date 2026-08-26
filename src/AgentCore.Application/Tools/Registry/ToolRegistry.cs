using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tools.Registry;

/// <summary>
/// Every tool the document declares, by id.
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, Lazy<AITool>> _tools;

    internal ToolRegistry(Dictionary<string, Lazy<AITool>> tools) => _tools = tools;

    /// <summary>Gets every id the sources registered.</summary>
    public IReadOnlyCollection<string> Ids => _tools.Keys;

    /// <summary>Reports whether one id is registered.</summary>
    /// <param name="id">The tool id.</param>
    /// <returns><see langword="true"/> when a source registered the id.</returns>
    public bool Contains(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _tools.ContainsKey(id);
    }

    /// <summary>Builds the tool one id names, or returns the one already built.</summary>
    /// <param name="id">The tool id.</param>
    /// <returns>The tool.</returns>
    /// <exception cref="KeyNotFoundException">No source registered the id.</exception>
    public AITool Resolve(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        return _tools.TryGetValue(id, out var tool)
            ? tool.Value
            : throw new KeyNotFoundException($"no tool source registered the tool id '{id}'.");
    }
}
