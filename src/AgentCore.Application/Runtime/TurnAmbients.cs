using AgentCore.Application.Ports;
using AgentCore.Application.State;
using AgentCore.Domain.Knowledge;

namespace AgentCore.Application.Runtime;

/// <summary>
/// Everything one turn makes ambient, carried as a single value on the flow of execution.
/// </summary>
internal sealed record TurnAmbients
{
    private static readonly TurnAmbients None = new();

    private static readonly AsyncLocal<TurnAmbients?> Ambient = new();

    /// <summary>Gets what is ambient on this flow, or <see langword="null"/> when nothing is open.</summary>
    internal static TurnAmbients? Current => Ambient.Value;

    /// <summary>Gets the state document the call reads and writes.</summary>
    public StateDocument? State { get; init; }

    /// <summary>Gets the screen this call draws on, or <see langword="null"/> when it has none.</summary>
    public IRenderPort? Screen { get; init; }

    /// <summary>Gets what to do with a tool failure the run reports.</summary>
    public Action<ToolFailure>? OnToolFailure { get; init; }

    /// <summary>Gets the turn the tools are running inside.</summary>
    public TurnContext? Context { get; init; }

    /// <summary>Gets what the turn may see of the knowledge base, or <see langword="null"/>.</summary>
    public KnowledgeScope? Knowledge { get; init; }

    /// <summary>Opens the four a turn owns, and carries every other ambient through unchanged.</summary>
    /// <param name="state">The state document the call reads and writes.</param>
    /// <param name="screen">The screen this call draws on, or <see langword="null"/> when it has none.</param>
    /// <param name="onToolFailure">What to do with a tool failure the run reports.</param>
    /// <param name="context">The turn the tools are running inside.</param>
    /// <returns>The scope. Disposing it puts back what was ambient before.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    internal static IDisposable Enter(
        StateDocument state,
        IRenderPort? screen,
        Action<ToolFailure> onToolFailure,
        TurnContext context)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(onToolFailure);
        ArgumentNullException.ThrowIfNull(context);

        return Push((Ambient.Value ?? None) with
        {
            State = state,
            Screen = screen,
            OnToolFailure = onToolFailure,
            Context = context,
        });
    }

    /// <summary>Opens one changed ambient over this flow, leaving the rest as they are.</summary>
    /// <param name="change">What the open scope holds, given what is ambient now.</param>
    /// <returns>The scope. Disposing it puts back what was ambient before.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="change"/> is <see langword="null"/>.</exception>
    internal static IDisposable Amend(Func<TurnAmbients, TurnAmbients> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        return Push(change(Ambient.Value ?? None));
    }

    private static Scope Push(TurnAmbients next)
    {
        var previous = Ambient.Value;
        Ambient.Value = next;
        return new Scope(previous);
    }

    /// <summary>One open scope. Disposing it puts back what was ambient before.</summary>
    private sealed class Scope : IDisposable
    {
        private readonly TurnAmbients? _previous;

        private bool _closed;

        public Scope(TurnAmbients? previous) => _previous = previous;

        public void Dispose()
        {
            if (_closed)
            {
                // A second dispose must not put an older value back over a newer scope.
                return;
            }

            _closed = true;
            Ambient.Value = _previous;
        }
    }
}
