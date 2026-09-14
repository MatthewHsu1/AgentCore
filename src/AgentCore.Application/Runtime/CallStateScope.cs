using System.Text.Json.Nodes;
using AgentCore.Application.State;

namespace AgentCore.Application.Runtime;

/// <summary>
/// The state of the call that runs on this flow of execution. An explicit open/close seam with
/// its own carrier: the loop opens the turn's document around every run, and a sessionless
/// graph opens its own around the run instead. Nothing reads the turn through this except the
/// guarded edges it was made for.
/// </summary>
public static class CallStateScope
{
    private static readonly AsyncLocal<StateDocument?> Flow = new();

    /// <summary>The message <see cref="Snapshot"/> throws with when no scope is open.</summary>
    internal const string NoScopeMessage =
        "A guarded graph edge asked for the state of the running call, and no call scope is open on "
        + "this flow of execution. Row 4 of the section 8.2 compile table reads the state of one call, "
        + "and CallSession opens that scope for the turn. Run the graph through a CallSession, or open "
        + "the scope with CallStateScope.Enter around the run. This throws and does not answer false, "
        + "because a guarded edge that quietly became unconditional is the silent graph failure "
        + "section 8.2 refuses to ship.";

    /// <summary>Opens the scope of one call over this flow of execution.</summary>
    /// <param name="state">The state of the call that runs now.</param>
    /// <returns>The scope. Disposing it puts back the state that was open before.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is <see langword="null"/>.</exception>
    public static IDisposable Enter(StateDocument state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var previous = Flow.Value;
        Flow.Value = state;
        return new Scope(previous);
    }

    /// <summary>Takes the snapshot a guarded graph edge reads.</summary>
    /// <returns>Every declared slot and the three reserved slots, for the call that runs now.</returns>
    /// <exception cref="InvalidOperationException">
    /// No scope is open on this flow of execution. The message names the fault and says what opens a
    /// scope.
    /// </exception>
    public static IReadOnlyDictionary<string, JsonNode?> Snapshot()
        => (Flow.Value ?? throw new InvalidOperationException(NoScopeMessage)).Snapshot();

    /// <summary>One open scope. Disposing it puts back what was open before.</summary>
    private sealed class Scope : IDisposable
    {
        private readonly StateDocument? _previous;

        private bool _closed;

        public Scope(StateDocument? previous) => _previous = previous;

        public void Dispose()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            Flow.Value = _previous;
        }
    }
}
