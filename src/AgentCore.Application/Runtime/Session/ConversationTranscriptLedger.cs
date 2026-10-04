using AgentCore.Application.Conversation;
using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Runtime.Harness;
using AgentCore.Application.Transcript;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime.Session
{
    internal sealed class ConversationTranscriptLedger
    {
        private readonly ConversationSession _session;

        internal ConversationTranscriptLedger(ConversationSession session)
        {
            ArgumentNullException.ThrowIfNull(session);
            _session = session;
            Reads = new ConversationStoreReads(session);
        }

        /// <summary>Gets the reads of what the message store holds beyond the session's words, and the count of its lost writes.</summary>
        internal ConversationStoreReads Reads { get; }

        /// <summary>
        /// Opens the session for one turn, and reads what the message store holds beyond its words. Changes no words the
        /// session already holds: the turn is not admitted yet, and a refused turn must leave them as they are.
        /// </summary>
        /// <param name="cancellationToken">Cancels the open and the reads.</param>
        /// <returns>
        /// The session, and what <see cref="CatchUpAsync"/> brings in once the turn is admitted: <see langword="null"/>
        /// for a session opened just now, or one whose words the message store already matches.
        /// </returns>
        internal async ValueTask<(AgentSession Session, TranscriptCatchUp? CatchUp)> OpenForTurnAsync(CancellationToken cancellationToken)
        {
            if (Session() is { } opened)
            {
                return (opened, await Reads.ReadAsync(opened, cancellationToken).ConfigureAwait(false));
            }

            return (await OpenSessionAsync(cancellationToken).ConfigureAwait(false), null);
        }

        /// <summary>
        /// Brings an admitted turn's session in line with what the message store held. A read made before the turn was admitted
        /// is stale once the session's own words moved, say because the turn before committed, so it is made again: no
        /// turn can commit now, and the words are never replaced by rows that lack them.
        /// </summary>
        /// <param name="opened">The session of this conversation.</param>
        /// <param name="catchUp">What <see cref="OpenForTurnAsync"/> read, or <see langword="null"/>.</param>
        /// <param name="cancellationToken">Cancels the reads.</param>
        /// <exception cref="InvalidOperationException">Another session's turn ended the conversation.</exception>
        internal async ValueTask CatchUpAsync(AgentSession opened, TranscriptCatchUp? catchUp, CancellationToken cancellationToken)
        {
            if (catchUp is not null)
            {
                await BringInAsync(opened, catchUp, cancellationToken).ConfigureAwait(false);
            }

            _session.Runner.RefuseIfComplete();
        }

        private async ValueTask BringInAsync(AgentSession opened, TranscriptCatchUp catchUp, CancellationToken cancellationToken)
        {
            if (!await TakeStoredAsync(opened, catchUp, StateToTake(catchUp), cancellationToken).ConfigureAwait(false))
            {
                if (await Reads.ReadAsync(opened, cancellationToken).ConfigureAwait(false) is not { } again
                    || !await TakeStoredAsync(opened, again, StateToTake(again), cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                catchUp = again;
            }

            Reads.CaughtUp(catchUp);
        }

        /// <summary>The state another session stored that this one takes, or <see langword="null"/> when it keeps its own.</summary>
        private ConversationSessionState? StateToTake(TranscriptCatchUp catchUp)
        {
            return catchUp.State is { } stored && (catchUp.Overtaken || stored.NextTurnIndex > _session.State.TurnIndex) ? stored : null;
        }

        /// <summary>
        /// Takes the words the message store holds, with the turn index, the stage, the slots, and the harness providers' state or
        /// a graph row's workflow another session stored beside them.
        /// </summary>
        /// <param name="opened">The session of this conversation.</param>
        /// <param name="catchUp">What the message store held.</param>
        /// <param name="stored">The state to take, or <see langword="null"/> when the session keeps its own.</param>
        /// <param name="cancellationToken">Cancels the session's rebuild.</param>
        /// <returns><see langword="false"/> when the session's words moved since the read and nothing was taken.</returns>
        private async ValueTask<bool> TakeStoredAsync(
            AgentSession opened, TranscriptCatchUp catchUp, ConversationSessionState? stored, CancellationToken cancellationToken)
        {
            if (stored is { Version: ConversationSessionState.CurrentVersion } current
                && _session.SessionCarriesHistory && _session.Compiled.HarnessStateKeys.Count > 0)
            {
                return await RebuildAsync(opened, catchUp, current, cancellationToken).ConfigureAwait(false);
            }

            ConversationSessionState? workflow = _session.ReusesGraphSession && stored is { Version: ConversationSessionState.CurrentVersion }
                ? stored
                : null;
            AgentSession? graph = workflow is null ? null : await GraphSessionFromAsync(workflow, cancellationToken).ConfigureAwait(false);

            if (!_session.History.TryResync(opened, catchUp))
            {
                return false;
            }

            lock (_session.TurnLock)
            {
                if (workflow is not null)
                {
                    _session.GraphSession = graph;
                    _session.GraphBlob = workflow.WorkflowState;
                }

                TakeState(catchUp, stored);
            }

            return true;
        }

        /// <summary>
        /// Replaces the session with one read from the state another session stored, holding the words the message store read,
        /// and releases the old one.
        /// </summary>
        /// <param name="opened">The session of this conversation.</param>
        /// <param name="catchUp">What the message store held.</param>
        /// <param name="stored">The state the conversation store holds.</param>
        /// <param name="cancellationToken">Cancels the read of the new session.</param>
        /// <returns><see langword="false"/> when the old session's words moved since the read and it was kept.</returns>
        private async ValueTask<bool> RebuildAsync(
            AgentSession opened, TranscriptCatchUp catchUp, ConversationSessionState stored, CancellationToken cancellationToken)
        {
            if (_session.History.Position(opened).Revision != catchUp.Revision)
            {
                return false;
            }

            AgentSession rebuilt = await SessionFromAsync(stored, cancellationToken).ConfigureAwait(false);

            lock (_session.TurnLock)
            {
                if (_session.History.Position(opened).Revision != catchUp.Revision)
                {
                    return false;
                }

                _ = _session.History.BeginConversation(
                    rebuilt,
                    _session.ConversationId,
                    catchUp.Rows,
                    Reads,
                    new TranscriptMarks(catchUp.NextOrdinal, stored.NextTurnIndex));

                _session.AgentSession = rebuilt;
                TakeState(catchUp, stored);
            }

            _session.Lifetime.ReleaseReplaced(opened);

            return true;
        }

        /// <summary>
        /// Moves the turn index on past the turns the message store holds, and takes the stage and slots another session stored.
        /// The caller holds <see cref="ConversationSession.TurnLock"/>, and swaps the session or the workflow in the same
        /// section: a snapshot must never pair one session's providers with another's turn index.
        /// </summary>
        /// <param name="catchUp">What the message store held.</param>
        /// <param name="stored">The state to take, or <see langword="null"/> when the session keeps its own.</param>
        private void TakeState(TranscriptCatchUp catchUp, ConversationSessionState? stored)
        {
            if (stored is not null)
            {
                _session.States.CatchUp(stored);
            }

            _session.State.TurnIndex = Math.Max(_session.State.TurnIndex, catchUp.NextTurnIndex);
        }

        /// <summary>Opens the session of this conversation, once, and hands the same session to every later caller.</summary>
        /// <param name="cancellationToken">Cancels the open.</param>
        /// <returns>The session.</returns>
        internal async ValueTask<AgentSession> OpenSessionAsync(CancellationToken cancellationToken)
        {
            if (Session() is { } opened)
            {
                return opened;
            }

            ConversationRecord record = await _session.Compiled.ConversationStore.CreateAsync(_session.ConversationId, cancellationToken).ConfigureAwait(false);

            ConversationSessionState? checkpoint;
            lock (_session.TurnLock)
            {
                checkpoint = _session.Checkpoint;
            }

            ConversationSessionState? stored = record.State ?? checkpoint;

            AgentSession session;
            if (_session.SessionCarriesHistory)
            {
                session = await SessionFromAsync(stored, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                session = new ConversationHistorySession();

                if (_session.ReusesGraphSession)
                {
                    _session.GraphSession = await GraphSessionFromAsync(stored, cancellationToken).ConfigureAwait(false);
                }
            }

            IReadOnlyList<ConversationMessage> spoken = record.LastMessageAt is null
                ? []
                : await _session.Compiled.ConversationStore.ReadForSessionAsync(_session.ConversationId, cancellationToken).ConfigureAwait(false);

            lock (_session.TurnLock)
            {
                if (_session.AgentSession is null)
                {
                    _session.AgentSession = session;

                    _session.State.TurnIndex = _session.History.BeginConversation(
                        session,
                        _session.ConversationId,
                        spoken,
                        Reads,
                        new TranscriptMarks(record.NextOrdinal, stored?.NextTurnIndex ?? 0));

                    SessionHooks hooks = _session.Hooks;
                    ConversationOrigin origin = hooks.OriginOf(storeHeldConversation: record.LastMessageAt is not null || stored is not null);

                    // No audit row follows conversation.ended, so a session that loads an ended
                    // conversation does not start it again, unless the restore refused the stored end.
                    bool storedEnded = stored is { IsComplete: true };
                    if (!storedEnded)
                    {
                        hooks.RaiseStarted(origin);
                    }

                    if (stored is { } s)
                    {
                        _session.States.Restore(s);
                    }

                    if (storedEnded && !_session.IsComplete)
                    {
                        hooks.RaiseStarted(origin);
                    }
                }

                return _session.AgentSession;
            }
        }

        /// <summary>The session a conversation that carries its history runs on: one read from the stored state, or a fresh one.</summary>
        private async ValueTask<AgentSession> SessionFromAsync(ConversationSessionState? stored, CancellationToken cancellationToken)
        {
            return stored is { Version: ConversationSessionState.CurrentVersion, Providers.Count: > 0 }
                ? await _session.Compiled.TurnAgent.DeserializeSessionAsync(
                    HarnessSessionState.Wrap(stored.Providers), cancellationToken: cancellationToken).ConfigureAwait(false)
                : await _session.Compiled.TurnAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>The workflow session a graph row that reuses one resumes from: the stored one, or a fresh one.</summary>
        private async ValueTask<AgentSession> GraphSessionFromAsync(ConversationSessionState? stored, CancellationToken cancellationToken)
        {
            return stored is { Version: ConversationSessionState.CurrentVersion, WorkflowState: { } blob }
                ? await _session.Compiled.TurnAgent.DeserializeSessionAsync(blob, cancellationToken: cancellationToken).ConfigureAwait(false)
                : await _session.Compiled.TurnAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Reads the session of this conversation, or null before its first turn opened one.
        /// </summary>
        /// <returns>The session.</returns>
        internal AgentSession? Session()
        {
            lock (_session.TurnLock)
            {
                return _session.AgentSession;
            }
        }

        /// <summary>
        /// Waits for every store write this conversation has queued.
        /// </summary>
        /// <returns>A task that completes when the words of the conversation are durable.</returns>
        internal async Task FlushTranscriptAsync()
        {
            if (Session() is { } session)
            {
                await _session.History.DrainAsync(session).ConfigureAwait(false);
            }

            await _session.Lifetime.StoredEnd.ConfigureAwait(false);
        }

        /// <summary>Builds the answer message for one queued approval request of this conversation.</summary>
        /// <param name="requestId">The id the caller answers.</param>
        /// <param name="approved">Whether the tool may run.</param>
        /// <returns>
        /// The user message carrying the approval response, or <see langword="null"/> when this conversation
        /// queues no request under that id: answered already, another conversation's, or never asked.
        /// </returns>
        internal ChatMessage? TryCreateApprovalAnswer(string requestId, bool approved)
        {
            ArgumentException.ThrowIfNullOrEmpty(requestId);

            return Session() is { } session
                ? PendingApprovalQueue.AnswerFor(session.StateBag.Serialize(), requestId, approved)
                : null;
        }

        /// <summary>The session a graph row's conversation holds, so the message store has somewhere to live.</summary>
        private sealed class ConversationHistorySession : AgentSession;
    }
}
