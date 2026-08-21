using AgentCore.Application.Tools;

namespace AgentCore.Application.Ports;

/// <summary>
/// One place tools come from.
/// </summary>
public interface IToolSource
{
    /// <summary>Names every tool this source serves.</summary>
    /// <param name="context">The document, and what a source resolves against.</param>
    /// <param name="cancellationToken">Cancels the discovery.</param>
    /// <returns>One registration for each tool, in any order.</returns>
    ValueTask<IReadOnlyList<ToolRegistration>> ProvideAsync(
        ToolSourceContext context, CancellationToken cancellationToken = default);
}
