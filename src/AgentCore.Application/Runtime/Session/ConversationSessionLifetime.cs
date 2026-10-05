using AgentCore.Application.Conversation;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime.Harness;
using AgentCore.Domain.Audit;
using Microsoft.Agents.AI;

namespace AgentCore.Application.Runtime.Session
{
    internal sealed class ConversationSessionLifetime
    {
        private readonly ConversationSession _session;

        private readonly Lock _replacedGate = new();

        private int _backgroundReleased;

        private volatile bool _owned;

        // Guarded by the session's turn lock.
        private bool _endUnsaved;

        private Task _replacedReleases = Task.CompletedTask;

        private Task _storedEnd = Task.CompletedTask;

        internal ConversationSessionLifetime(ConversationSession session)
        {
            ArgumentNullException.ThrowIfNull(session);
            _session = session;
            Ending = new ConversationEnding(session);
            Cleanup = new ConversationCleanup(session);
        }

        /// <summary>
        /// Marks this session as held by a session owner, which unloads it with <see cref="DisposeAsync(UnloadCause?)"/>.
        /// A plain <see cref="DisposeAsync()"/> then leaves the mailbox pin to that unload.
        /// </summary>
        internal void MarkOwned()
        {
            _owned = true;
        }

        /// <summary>Gets what raises this conversation's <c>ConversationEnded</c>.</summary>
        internal ConversationEnding Ending { get; }

        /// <summary>Gets what deletes this conversation's workspace, and disposes its shells, after its tools.</summary>
        internal ConversationCleanup Cleanup { get; }

        /// <summary>Closes the conversation, and asks for <see cref="ConversationEnded"/>.</summary>
        /// <param name="reason">Why the conversation ended, as one member of the closed set.</param>
        /// <param name="cause">The transport's word for why its call ended, or <see langword="null"/>.</param>
        /// <returns>
        /// <see langword="true"/> when this call was the first end of the conversation, and <see langword="false"/>
        /// when the conversation had already ended.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="reason"/> is not a member of the closed set.
        /// </exception>
        internal bool EndConversation(ConversationEndReason reason, string? cause = null)
        {
            // The token is read first, so a value outside the closed set ends nothing.
            _ = ConversationEndReasons.ToToken(reason);

            // Complete before the end reads the turn slot: under the turn lock, a turn that takes the slot after
            // that read sees it and is refused, and one that took it before is the turn the end waits for.
            _session.IsComplete = true;
            bool first = Ending.Request(reason, cause);
            SaveEnd();

            _ = Cleanup.AfterToolsAsync(shells: false);
            return first;
        }

        /// <summary>
        /// Stores that the conversation is over, so a session that loads it later refuses its turns. A running turn
        /// may have read its state before the end, so the write waits for that turn to free its slot and then lands
        /// behind the turn's own append (<see cref="SaveEndAfterTurn"/>).
        /// </summary>
        private void SaveEnd()
        {
            lock (_session.TurnLock)
            {
                if (_session.Cuts.RunningTurnIndex() is not null)
                {
                    _endUnsaved = true;
                    return;
                }

                SaveState();
            }
        }

        /// <summary>Writes an end that waited on the turn now freeing its slot. The cut tracker calls this under the turn lock.</summary>
        internal void SaveEndAfterTurn()
        {
            if (_endUnsaved)
            {
                _endUnsaved = false;
                SaveState();
            }
        }

        /// <summary>Gets what completes once an end written while no session of this conversation was open landed.</summary>
        internal Task StoredEnd => Volatile.Read(ref _storedEnd);

        private void SaveState()
        {
            if (_session.Ledger.Session() is { } session)
            {
                _session.History.SaveState(session, _session.States.Snapshot());
                return;
            }

            Volatile.Write(ref _storedEnd, SaveStoredEndAsync());
        }

        // No turn of this session opened the conversation, so the end amends the state the conversation store holds: the stage, the
        // slots and the turn index of the session that last ran a turn stay as they were.
        private async Task SaveStoredEndAsync()
        {
            IConversationStore store = _session.Compiled.ConversationStore;
            try
            {
                if (await store.GetAsync(_session.ConversationId).ConfigureAwait(false) is { } record)
                {
                    ConversationSessionState basis = record.State ?? _session.States.Snapshot();
                    await store.SaveStateAsync(_session.ConversationId, basis with { IsComplete = true }).ConfigureAwait(false);
                }
            }
#pragma warning disable CA1031 // Nothing awaits this write but a flush or a dispose, and a refused write never ends anything.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                Log.TranscriptWriteFailed(_session.Logger, _session.ConversationId, _session.State.TurnIndex, exception);
            }
        }

        /// <summary>
        /// Deletes this conversation's workspace, if it has one. Called by <see cref="Cleanup"/> once the conversation
        /// ended and its tools are done, and by the session owner after it closed or unloaded the session.
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
        /// conversation's background sessions, waits for the release of every session a catch-up replaced and for its
        /// running tools (at most <see cref="ConversationEnding.ToolGrace"/>), and disposes its shell executors.
        /// Idempotent: a session already disposed, or one that never had either, disposes nothing.
        /// </summary>
        internal ValueTask DisposeAsync()
        {
            return DisposeAsync(unloaded: null);
        }

        /// <summary>
        /// Disposes as <see cref="DisposeAsync()"/> does. For a session its owner unloads, it then raises
        /// <see cref="ConversationUnloaded"/>, last, whether or not a step threw, while the session still pins its
        /// mailbox. An end that waited on the last turn was raised when that turn freed its slot, so it comes first.
        /// </summary>
        /// <param name="unloaded">Why the owner unloads the session, or <see langword="null"/> for a plain dispose.</param>
        internal async ValueTask DisposeAsync(UnloadCause? unloaded)
        {
            try
            {
                Ending.StopToolsAfterGrace();
                await _session.Cuts.CloseTurnsAsync().ConfigureAwait(false);
                await ReleaseBackgroundSessionsAsync().ConfigureAwait(false);
                await ReplacedReleases().ConfigureAwait(false);
                _ = await Task.WhenAny(_session.ToolRuns.WhenIdle(), Ending.WhenToolsStopped).ConfigureAwait(false);
                await DisposeShellsAsync().ConfigureAwait(false);
                await Cleanup.Pending.ConfigureAwait(false);
                await StoredEnd.ConfigureAwait(false);
            }
            finally
            {
                if (unloaded is { } cause)
                {
                    SessionHooks hooks = _session.Hooks;
                    _ = hooks.Raise(new ConversationUnloaded(hooks.Scope(turnIndex: null, stage: null), cause));
                }

                // A host may dispose a session its owner still holds; the owner's unload then comes later, inside the pin.
                if (unloaded is not null || !_owned)
                {
                    _session.Hooks.Release();
                    Ending.Dispose();
                }
            }
        }

        /// <summary>
        /// Starts the release of a session a catch-up replaced, off the turn path, and keeps it for
        /// <see cref="DisposeAsync()"/> to wait for. Its outcome is logged, never thrown.
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
        /// only the turn-end path and <see cref="DisposeAsync()"/> tear it down.
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
