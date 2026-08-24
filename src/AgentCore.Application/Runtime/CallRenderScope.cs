using AgentCore.Application.Ports;

namespace AgentCore.Application.Runtime;

/// <summary>
/// The screen of the call that runs on this flow of execution.
/// </summary>
public static class CallRenderScope
{
    /// <summary>The screen of the call that runs now, or <see langword="null"/> when it has none.</summary>
    public static IRenderPort? Current => TurnAmbients.Current?.Screen;

    /// <summary>Opens the screen of one call over this flow of execution.</summary>
    /// <param name="port">The screen, or <see langword="null"/> for a call that has none.</param>
    /// <returns>The scope. Disposing it puts back the screen that was ambient before.</returns>
    public static IDisposable Enter(IRenderPort? port)
        => TurnAmbients.Amend(ambients => ambients with { Screen = port });
}
