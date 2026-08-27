using AgentCore.Application.Ports;

namespace AgentCore.Application.Runtime;

/// <summary>
/// The screen of the call that runs on this flow of execution.
/// </summary>
public static class CallRenderScope
{
    /// <summary>The screen of the call that runs now, or <see langword="null"/> when it has none.</summary>
    public static IRenderPort? Current => TurnAmbients.Current?.Screen;
}
