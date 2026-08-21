using AgentCore.Application.Ports;

namespace AgentCore.Application.Runtime;

/// <summary>
/// The screen of the call that runs on this flow of execution.
/// </summary>
public static class CallRenderScope
{
    private static readonly AsyncLocal<IRenderPort?> Ambient = new();

    /// <summary>The screen of the call that runs now, or <see langword="null"/> when it has none.</summary>
    public static IRenderPort? Current => Ambient.Value;

    /// <summary>Opens the screen of one call over this flow of execution.</summary>
    /// <param name="port">The screen, or <see langword="null"/> for a call that has none.</param>
    /// <returns>The scope. Disposing it puts back the screen that was ambient before.</returns>
    public static IDisposable Enter(IRenderPort? port)
    {
        var previous = Ambient.Value;
        Ambient.Value = port;
        return new Scope(previous);
    }

    /// <summary>One open scope. Disposing it puts back the screen that was ambient before.</summary>
    private sealed class Scope : IDisposable
    {
        private readonly IRenderPort? _previous;

        private bool _closed;

        public Scope(IRenderPort? previous) => _previous = previous;

        public void Dispose()
        {
            if (_closed)
            {
                // A second dispose must not put an older screen back over a newer scope.
                return;
            }

            _closed = true;
            Ambient.Value = _previous;
        }
    }
}
