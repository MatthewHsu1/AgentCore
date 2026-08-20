using System.Text.Json.Nodes;
using AgentCore.Application.State;

namespace AgentCore.Application.Runtime;

/// <summary>
/// The state of the call that runs on this flow of execution.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam between two rules that pull apart. T44 makes the compiled agent a process
/// singleton, so row 4 of the section 8.2 compile table holds one workflow graph that every call
/// shares, and a guarded edge lives inside it. A guarded edge reads the state of one call, and each
/// call owns its own <see cref="StateDocument"/>. Nothing per call may therefore be captured when the
/// graph compiles. The edge predicate finds the state instead, and it finds it here.
/// </para>
/// <para>
/// The carrier is <see cref="AsyncLocal{T}"/>, so the value follows the flow of execution and not the
/// thread. A workflow that continues on another thread pool thread still reads the state of its own
/// call, and 26 turns that run at the same time each read their own. This holds no lock and shares
/// nothing: one flow reads one value.
/// </para>
/// <para>
/// <see cref="CallSession"/> opens the scope for the turn and closes it after. The scope closes when
/// the turn throws, when the turn is cancelled, and when the caller interrupts, because a
/// <see langword="using"/> statement carries all three.
/// </para>
/// <para>
/// <see cref="Snapshot"/> throws when no scope is open. It never answers <see langword="false"/> for
/// the edge, because a guarded edge that quietly became unconditional is exactly the silent graph
/// failure section 8.2 refuses to ship.
/// </para>
/// <para>
/// One turn writes its state before the turn starts and after the run ends, never while the run is
/// in flight, so the snapshot the edge reads needs no lock either.
/// </para>
/// </remarks>
public static class CallStateScope
{
    /// <summary>The message <see cref="Snapshot"/> throws with when no scope is open.</summary>
    /// <remarks>
    /// It is a constant so a test asserts the message a host reads, rather than a copy of it. It stays
    /// internal, because a host reads this message and never matches it. D15 makes every public member
    /// a compatibility obligation, and the value of a public constant is one as well.
    /// </remarks>
    internal const string NoScopeMessage =
        "A guarded graph edge asked for the state of the running call, and no call scope is open on "
        + "this flow of execution. Row 4 of the section 8.2 compile table reads the state of one call, "
        + "and CallSession opens that scope for the turn. Run the graph through a CallSession, or open "
        + "the scope with CallStateScope.Enter around the run. This throws and does not answer false, "
        + "because a guarded edge that quietly became unconditional is the silent graph failure "
        + "section 8.2 refuses to ship.";

    private static readonly AsyncLocal<StateDocument?> Ambient = new();

    /// <summary>Opens the scope of one call over this flow of execution.</summary>
    /// <param name="state">The state of the call that runs now.</param>
    /// <returns>The scope. Disposing it puts back the state that was ambient before.</returns>
    /// <remarks>
    /// An async iterator restores the execution context of its caller at every <c>yield return</c>, so
    /// an ambient value does not survive one. A caller that yields between two runs opens the scope
    /// again for each run, which is what <see cref="CallSession.RunTurnStreamingAsync"/> does.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is <see langword="null"/>.</exception>
    public static IDisposable Enter(StateDocument state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var previous = Ambient.Value;
        Ambient.Value = state;
        return new Scope(previous);
    }

    /// <summary>Takes the snapshot a guarded graph edge reads.</summary>
    /// <returns>Every declared slot and the three reserved slots, for the call that runs now.</returns>
    /// <exception cref="InvalidOperationException">
    /// No scope is open on this flow of execution. The message names the fault and says what opens a
    /// scope.
    /// </exception>
    public static IReadOnlyDictionary<string, JsonNode?> Snapshot()
        => (Ambient.Value ?? throw new InvalidOperationException(NoScopeMessage)).Snapshot();

    /// <summary>One open scope. Disposing it puts back the state that was ambient before.</summary>
    private sealed class Scope : IDisposable
    {
        private readonly StateDocument? _previous;
        private bool _closed;

        public Scope(StateDocument? previous) => _previous = previous;

        public void Dispose()
        {
            if (_closed)
            {
                // A second dispose must not put an older state back over a newer scope.
                return;
            }

            _closed = true;
            Ambient.Value = _previous;
        }
    }
}
