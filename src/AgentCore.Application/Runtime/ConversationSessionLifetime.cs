using AgentCore.Application.Diagnostics;
using AgentCore.Application.Runtime.Harness;
using AgentCore.Domain.Audit;
using Microsoft.Agents.AI;

namespace AgentCore.Application.Runtime
{
    internal sealed class ConversationSessionLifetime
    {
        private readonly ConversationSession _session;

        private readonly Lock _replacedGate = new();

        private int _backgroundReleased;

        private Task _replacedReleases = Task.CompletedTask;

        internal ConversationSessionLifetime(ConversationSession session)
        {
            ArgumentNullException.ThrowIfNull(session);
            _session = session;
        }

        /// <summary>Closes the conversation, and writes the last event of its chain.</summary>
        /// <param name="reason">Why the conversation ended, as one member of the closed set.</param>
        /// <returns>
        /// <see langword="true"/> when this conversation wrote the event, and <see langword="false"/> when the
        /// conversation had already ended.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="reason"/> is not a member of the closed set.
        /// </exception>
        internal bool EndConversation(ConversationEndReason reason)
        {
            bool wrote = _session.Events.EndConversation(reason, _session.Time.GetUtcNow());

            _session.IsComplete = true;

            DeleteWorkspace();
            return wrote;
        }

        /// <summary>
        /// Deletes this conversation's workspace, if it has one. Called when the conversation ends, and by the
        /// session owner when it closes or unloads the session.
        /// </summary>
        internal void DeleteWorkspace()
        {
            _session.WorkspaceRoot?.Delete(_session.Logger);
        }

        /// <summary>
        /// Stamps this conversation's workspace with now, if it has one. Called at the start of every turn, and by
        /// the held-session idle poll while a running turn or a background child keeps the session alive past its
        /// idle timeout, so a boot sweep sharing the workspace root never mistakes an active folder for a dead one.
        /// </summary>
        internal void TouchWorkspace()
        {
            _session.WorkspaceRoot?.Touch(_session.Time, _session.Logger);
        }

        /// <summary>
        /// Refuses every later turn, waits for a turn still holding the conversation to end, then disposes this
        /// conversation's background sessions and shell executors, and waits for the release of every session a
        /// catch-up replaced. Idempotent: a session already disposed, or one that never had either, disposes nothing.
        /// </summary>
        internal async ValueTask DisposeAsync()
        {
            await _session.Cuts.CloseTurnsAsync().ConfigureAwait(false);
            await ReleaseBackgroundSessionsAsync().ConfigureAwait(false);
            await ReplacedReleases().ConfigureAwait(false);
            await DisposeShellsAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Starts the release of a session a catch-up replaced, off the turn path, and keeps it for
        /// <see cref="DisposeAsync"/> to wait for. Its outcome is logged, never thrown.
        /// </summary>
        /// <param name="replaced">The session the catch-up swapped out. Nothing may read it any more.</param>
        internal void ReleaseReplaced(AgentSession replaced)
        {
            if (_session.Compiled.BackgroundProviders.Count == 0)
            {
                return;
            }

            Task release = Task.Run(() => ReleaseReplacedAsync(replaced));

            lock (_replacedGate)
            {
                _replacedReleases = Task.WhenAll(_replacedReleases, release);
            }
        }

        private async Task ReleaseReplacedAsync(AgentSession replaced)
        {
            try
            {
                await BackgroundSessionRelease.ReleaseAsync(
                    _session.Compiled.BackgroundProviders, replaced, _session.ConversationId, _session.Logger, CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Nothing awaits this release until the conversation is disposed, so nothing may escape it.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                SessionOwnerLog.BackgroundReleaseFailed(_session.Logger, _session.ConversationId, exception);
            }
        }

        private Task ReplacedReleases()
        {
            lock (_replacedGate)
            {
                return _replacedReleases;
            }
        }

        /// <summary>
        /// Cancels the background children still running on this conversation's session and releases it, once
        /// per conversation. Skipped when the conversation never opened a session or the document names no background
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
                _session.Compiled.BackgroundProviders, session, _session.ConversationId, _session.Logger, CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>
        /// Disposes this conversation's shell executors, if it has any. <see cref="EndConversation"/> does not conversation
        /// this: a tool that ends the conversation mid-turn still needs the shell for the rest of that turn, so
        /// only the turn-end path and <see cref="DisposeAsync"/> tear it down.
        /// </summary>
        internal ValueTask DisposeShellsAsync()
        {
            return _session.Shells?.DisposeAsync() ?? ValueTask.CompletedTask;
        }

        /// <summary>
        /// Gets whether this conversation's document declares any <c>background:</c> children at all, whether or
        /// not one has ever actually run. The held-session idle check reads this to decide whether it must poll
        /// for a running child at all, or can keep the single sleep to the idle timeout it always had.
        /// </summary>
        internal bool MayHaveBackgroundChildren => _session.Compiled.BackgroundProviders.Count > 0;

        /// <summary>
        /// Gets whether a <c>background:</c> child this conversation started is still running. The held-session
        /// idle check polls this to keep the session while a child outlives the turn that started it (issue #31).
        /// </summary>
        /// <remarks>
        /// <see cref="BackgroundAgentsProvider.GetIncompleteTasks"/> is a snapshot, not something a caller can
        /// await, so this only answers "right now": the poller in <c>HeldSession</c> calls it again later. A
        /// provider that cannot read its own session state — a state bag shaped for a different provider version,
        /// say — throws instead of answering; that is read as "still running" rather than let the idle timer
        /// unload a session out from under a child that, for all this can tell, is still there.
        /// </remarks>
#pragma warning disable MAAI001 // BackgroundAgentsProvider is evaluation-only in Microsoft.Agents.AI 1.21.0.
        internal bool HasRunningBackgroundChild()
        {
            if (_session.Compiled.BackgroundProviders.Count == 0 || _session.Ledger.Session() is not { } session)
            {
                return false;
            }

            foreach (BackgroundAgentsProvider provider in _session.Compiled.BackgroundProviders)
            {
                try
                {
                    if (provider.GetIncompleteTasks(session).Count > 0)
                    {
                        return true;
                    }
                }
#pragma warning disable CA1031 // The idle timer thread must never crash on a provider's snapshot fault.
                catch (Exception exception)
#pragma warning restore CA1031
                {
                    SessionOwnerLog.BackgroundChildCheckFailed(_session.Logger, _session.ConversationId, exception);
                    return true;
                }
            }

            return false;
        }
#pragma warning restore MAAI001
    }
}
