using AgentCore.Application.Ports;

namespace AgentCore.Application.Runtime;

/// <summary>Where a producer cites a source for the call that runs now.</summary>
public static class CallSourceScope
{
    /// <summary>The sources of the call that runs now, or <see langword="null"/> when none is open.</summary>
    public static ISourcePort? Current => TurnAmbients.Current?.Sources;
}
