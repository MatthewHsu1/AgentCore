using AgentCore.Domain.Audit;

namespace AgentCore.Application.Runtime;

/// <summary>One tool call that did not run to completion, as the function-invocation loop saw it.</summary>
/// <remarks>
/// It is neutral, for the reason <see cref="CallEvent"/> is: it carries no sequence, no call id, and no
/// wire token. <see cref="CallSession"/> turns it into a fact of the call, because the session is the
/// only thing that knows the turn index and the ordinal a later amendment must reference.
/// </remarks>
internal sealed record ToolFailure
{
    /// <summary>Gets the name the MODEL called.</summary>
    /// <remarks>
    /// For <see cref="ToolFailureKind.Undeclared"/> this is a name the document does not declare, which
    /// is the whole finding, so it is never corrected to a declared one.
    /// </remarks>
    public required string ToolName { get; init; }

    /// <summary>Gets the id the model gave this one call.</summary>
    /// <remarks>
    /// Three calls to one tool in one assistant message are one name and three ids, so this is the only
    /// thing that tells them apart. See <see cref="AuditPayloadKeys.ToolCallId"/>.
    /// </remarks>
    public required string ToolCallId { get; init; }

    /// <summary>Gets which of the two ways a tool call fails this one was.</summary>
    public required ToolFailureKind Kind { get; init; }

    /// <summary>Gets what went wrong, in one sentence. It never holds a secret value.</summary>
    public required string Message { get; init; }
}

/// <summary>
/// Where a tool failure is reported on this flow of execution.
/// </summary>
/// <remarks>
/// <para>
/// This is the same seam <see cref="CallStateScope"/> is, opened for the same reason. T44 makes the
/// compiled agent a process singleton, so the chat client that invokes tools is shared by every call
/// and nothing per call may be captured when it is built. The observer that watches tool invocation is
/// therefore built once and finds the call HERE, on the flow of execution the turn runs on.
/// </para>
/// <para>
/// The carrier is <see cref="AsyncLocal{T}"/>, so the value follows the flow and not the thread: 26
/// turns that run at the same time each report to their own session, the framework's own tool threads
/// included, and nothing here takes a lock or shares anything.
/// </para>
/// <para>
/// <b>An absent listener is silence, and not a failure.</b> This is the one place it differs from
/// <see cref="CallStateScope"/>, which throws instead. The difference is what is lost: a guarded edge
/// with no state would quietly become unconditional and change what the agent DOES, which section 8.2
/// refuses to ship, while a tool invoked outside any session — a compile-time probe, a test that runs
/// an agent directly — has no call to record against and nothing to record. An observer must never be
/// the thing that breaks the run it observes.
/// </para>
/// </remarks>
internal static class ToolFailureScope
{
    private static readonly AsyncLocal<Action<ToolFailure>?> Ambient = new();

    /// <summary>Opens the scope of one turn over this flow of execution.</summary>
    /// <param name="listener">What to do with each failure. It is called on the framework's tool flow.</param>
    /// <returns>The scope. Disposing it puts back the listener that was ambient before.</returns>
    /// <remarks>
    /// An async iterator restores the execution context of its caller at every <c>yield return</c>, so
    /// an ambient value does not survive one. A caller that yields between two rounds opens the scope
    /// again for each round, which is what <see cref="CallSession.RunTurnStreamingAsync"/> does.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="listener"/> is <see langword="null"/>.</exception>
    internal static IDisposable Enter(Action<ToolFailure> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);

        var previous = Ambient.Value;
        Ambient.Value = listener;
        return new Scope(previous);
    }

    /// <summary>Reports one tool failure to the call running on this flow of execution.</summary>
    /// <param name="failure">What did not run to completion.</param>
    /// <remarks>
    /// It does nothing when no scope is open. See the remarks on <see cref="ToolFailureScope"/> for why
    /// that is silence and not a fault.
    /// </remarks>
    internal static void Report(ToolFailure failure) => Ambient.Value?.Invoke(failure);

    /// <summary>One open scope. Disposing it puts back the listener that was ambient before.</summary>
    private sealed class Scope : IDisposable
    {
        private readonly Action<ToolFailure>? _previous;
        private bool _closed;

        public Scope(Action<ToolFailure>? previous) => _previous = previous;

        public void Dispose()
        {
            if (_closed)
            {
                // A second dispose must not put an older listener back over a newer scope.
                return;
            }

            _closed = true;
            Ambient.Value = _previous;
        }
    }
}
