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
        /// Deletes this conversation's workspace, if it has one. Called once the chain has recorded why the
        /// conversation ended, from every path that can end it.
        /// </summary>
        internal void DeleteWorkspace()
        {
            _session.WorkspaceRoot?.Delete(_session.Logger);
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
                Log.BackgroundReleaseFailed(_session.Logger, _session.ConversationId, exception);
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
    }
}
