using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

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

    /// <summary>Gets the tools a delegated run of this call is offered, or <see langword="null"/> for none.</summary>
    public IReadOnlyList<AITool>? Tools { get; init; }

    /// <summary>Gets the id of the delegating tool <see cref="Tools"/> are meant for.</summary>
    public string? ToolsFor { get; init; }
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

    /// <summary>Reads the tools offered to a run delegated through one tool of this call.</summary>
    /// <param name="delegatingToolId">
    /// The tool whose invocation this run sits under, or <see langword="null"/> when it sits under
    /// none — which is every run the caller's own agent makes.
    /// </param>
    /// <returns>The tools, or <see langword="null"/> when this run is offered none.</returns>
    internal static IReadOnlyList<AITool>? ToolsFor(string? delegatingToolId)
        => delegatingToolId is not null
            && Ambient.Value is { Tools: { Count: > 0 } tools, ToolsFor: { } gate }
            && string.Equals(gate, delegatingToolId, StringComparison.Ordinal)
                ? tools
                : null;

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
