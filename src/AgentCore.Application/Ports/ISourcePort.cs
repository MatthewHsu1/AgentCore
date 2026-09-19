using AgentCore.Domain.Sources;

namespace AgentCore.Application.Ports;

/// <summary>
/// Carries where an answer came from, from whatever produced it to the host that shows it.
/// </summary>
public interface ISourcePort
{
    /// <summary>Cites one source for the running conversation.</summary>
    /// <param name="source">Where the answer came from.</param>
    void Publish(SourceReference source);
}
