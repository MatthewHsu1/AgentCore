using AgentCore.Application.Runtime.Harness;
using AgentCore.Domain.Audit;

namespace AgentCore.Application.Runtime;

internal sealed class CallSessionLifetime
{
    private readonly CallSession _session;

    // The background release runs once per call: both DisposeAsync and the turn-end path reach
    // it, and a second release would re-cancel children that already went away.
    private int _backgroundReleased;

    internal CallSessionLifetime(CallSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <summary>Closes the call, and writes the last event of its chain.</summary>
    /// <param name="reason">Why the call ended, as one member of the closed set.</param>
    /// <returns>
    /// <see langword="true"/> when this call wrote the event, and <see langword="false"/> when the
    /// call had already ended.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="reason"/> is not a member of the closed set.
    /// </exception>
    internal bool EndCall(CallEndReason reason)
    {
        // The event is written before the flag moves. A value outside the closed set therefore ends
        // no call and writes nothing.
        var wrote = _session.Events.EndCall(reason, _session.Time.GetUtcNow());
        _session.IsComplete = true;
        DeleteWorkspace();
        return wrote;
    }

    /// <summary>
    /// Deletes this call's workspace, if it has one. Called once the chain has recorded why the
    /// call ended, from every path that can end it.
    /// </summary>
    internal void DeleteWorkspace() => _session.WorkspaceRoot?.Delete(_session.Logger);

    /// <summary>
    /// Disposes this call's background sessions and shell executors. Idempotent: a session already
    /// disposed, or one that never had either, disposes nothing.
    /// </summary>
    internal async ValueTask DisposeAsync()
    {
        await ReleaseBackgroundSessionsAsync().ConfigureAwait(false);
        await DisposeShellsAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Cancels the background children still running on this call's session and releases it, once
    /// per call. Skipped when the call never opened a session or the document names no background
    /// children. Release precedes the shells: a child cancelled here may be inside a shell tool.
    /// </summary>
    internal async ValueTask ReleaseBackgroundSessionsAsync()
    {
        if (Interlocked.Exchange(ref _backgroundReleased, 1) == 1
            || _session.Compiled.BackgroundProviders.Count == 0
            || _session.Ledger.Session() is not { } session)
        {
            return;
        }

        await BackgroundSessionRelease.ReleaseAsync(
            _session.Compiled.BackgroundProviders, session, _session.CallId, _session.Logger, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Disposes this call's shell executors, if it has any. <see cref="EndCall"/> does not call
    /// this: a tool that ends the call mid-turn still needs the shell for the rest of that turn, so
    /// only the turn-end path and <see cref="DisposeAsync"/> tear it down.
    /// </summary>
    internal ValueTask DisposeShellsAsync() => _session.Shells?.DisposeAsync() ?? ValueTask.CompletedTask;
}
