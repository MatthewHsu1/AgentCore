using Microsoft.Agents.AI;

namespace AgentCore.Application.Runtime;

/// <summary>
/// What one turn adds to the model invocation, on top of what the compiled agent already carries.
/// </summary>
internal sealed record TurnContext
{
    /// <summary>Gets the session the turn runs on. The context applies to a run on that session alone.</summary>
    public required AgentSession Session { get; init; }

    /// <summary>Gets the instructions for this one invocation, or <see langword="null"/> for none.</summary>
    public string? Instructions { get; init; }
}

/// <summary>
/// The turn whose per-invocation context runs on this flow of execution.
/// </summary>
internal static class TurnContextScope
{
    private static readonly AsyncLocal<TurnContext?> Ambient = new();

    /// <summary>Opens the context of one turn over this flow of execution.</summary>
    /// <param name="context">What this turn adds to its own model invocation.</param>
    /// <returns>The scope. Disposing it puts back the context that was ambient before.</returns>
    /// <remarks>
    /// An async iterator restores the execution context of its caller at every <c>yield return</c>, so
    /// an ambient value does not survive one. A caller that yields between two rounds opens the scope
    /// again for each round, which is what <see cref="CallSession.RunTurnStreamingAsync"/> does.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    internal static IDisposable Enter(TurnContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var previous = Ambient.Value;
        Ambient.Value = context;
        return new Scope(previous);
    }

    /// <summary>Reads the context of the turn a run on one session belongs to.</summary>
    /// <param name="session">The session the framework is about to run on.</param>
    /// <returns>The context, or <see langword="null"/> when this run is not that turn's own.</returns>
    internal static TurnContext? For(AgentSession? session)
        => Ambient.Value is { } context && ReferenceEquals(context.Session, session) ? context : null;

    /// <summary>One open scope. Disposing it puts back the context that was ambient before.</summary>
    private sealed class Scope : IDisposable
    {
        private readonly TurnContext? _previous;
        private bool _closed;

        public Scope(TurnContext? previous) => _previous = previous;

        public void Dispose()
        {
            if (_closed)
            {
                // A second dispose must not put an older context back over a newer scope.
                return;
            }

            _closed = true;
            Ambient.Value = _previous;
        }
    }
}
