using AgentCore.Application.Conversation;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Runtime.Harness;
using AgentCore.Application.Transcript;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime;

internal sealed class ConversationTranscriptLedger
{
    private readonly ConversationSession _session;

    internal ConversationTranscriptLedger(ConversationSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <summary>
    /// Opens the session of this conversation, once, and re-syncs it with store 0 before handing it to every
    /// later turn.
    /// </summary>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The session.</returns>
    internal async ValueTask<AgentSession> OpenSessionAsync(CancellationToken cancellationToken)
    {
        if (Session() is { } opened)
        {
            await ResyncTranscriptAsync(opened, cancellationToken).ConfigureAwait(false);
            return opened;
        }

        var record = await _session.Compiled.ConversationStore.CreateAsync(_session.ConversationId, cancellationToken).ConfigureAwait(false);

        ConversationSessionState? checkpoint;
        lock (_session.InterruptLock)
        {
            checkpoint = _session.Checkpoint;
        }

        var stored = record.State ?? checkpoint;

        AgentSession session;
        if (_session.SessionCarriesHistory)
        {
            session = stored is { Version: ConversationSessionState.CurrentVersion, Providers.Count: > 0 }
                ? await _session.Compiled.TurnAgent.DeserializeSessionAsync(
                    HarnessSessionState.Wrap(stored.Providers), cancellationToken: cancellationToken).ConfigureAwait(false)
                : await _session.Compiled.TurnAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            session = new ConversationHistorySession();

            if (_session.ReusesGraphSession)
            {
                _session.GraphSession = stored is { Version: ConversationSessionState.CurrentVersion, WorkflowState: { } blob }
                    ? await _session.Compiled.TurnAgent.DeserializeSessionAsync(blob, cancellationToken: cancellationToken).ConfigureAwait(false)
                    : await _session.Compiled.TurnAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        IReadOnlyList<ConversationMessage> spoken = record.LastMessageAt is null
            ? []
            : await _session.Compiled.ConversationStore.ReadForSessionAsync(_session.ConversationId, cancellationToken).ConfigureAwait(false);

        lock (_session.InterruptLock)
        {
            if (_session.AgentSession is null)
            {
                _session.AgentSession = session;

                _session.State.TurnIndex = _session.History.BeginConversation(
                    session,
                    _session.ConversationId,
                    spoken,
                    _session.Events.RaiseDroppedTranscriptWrite,
                    new TranscriptMarks(record.NextOrdinal, stored?.NextTurnIndex ?? 0));

                if (stored is { } s)
                {
                    _session.States.Restore(s);
                }
            }

            return _session.AgentSession;
        }
    }

    /// <summary>
    /// Brings the session's words back in line with store 1 before a turn after the first, when and
    /// only when someone else wrote to the conversation in between.
    /// </summary>
    /// <param name="opened">The session of this conversation.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    private async ValueTask ResyncTranscriptAsync(AgentSession opened, CancellationToken cancellationToken)
    {
        await _session.History.DrainAsync(opened).ConfigureAwait(false);

        try
        {
            var refreshed = await RecordAsync("resync against", cancellationToken).ConfigureAwait(false);

            if (refreshed.NextOrdinal == _session.History.NextOrdinal(opened))
            {
                return;
            }

            var rows = await _session.Compiled.ConversationStore.ReadForSessionAsync(
                _session.ConversationId, cancellationToken).ConfigureAwait(false);

            _session.History.Resync(opened, rows, refreshed.NextOrdinal);
        }
#pragma warning disable CA1031 // A store that cannot be read never ends a conversation: the turn runs on the words the session holds.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            Log.TranscriptResyncFailed(_session.Logger, _session.ConversationId, _session.State.TurnIndex, exception);
            _session.Events.RaiseFailedTranscriptResync(_session.State.TurnIndex, exception);
        }
    }

    /// <summary>
    /// Cuts an edit that reaches under the summary: the parent is a row the summary stands in for,
    /// or the edit takes the whole conversation. The caller asks
    /// <see cref="AgentCoreChatHistoryProvider.CanTruncateFrom"/> first and comes here when it says no.
    /// </summary>
    /// <param name="opened">The session of this conversation.</param>
    /// <param name="parentMessageId">The message the edit hangs off, or <see langword="null"/> for the whole conversation.</param>
    /// <param name="cancellationToken">Cancels the cut.</param>
    /// <returns>The turns the cut withdrew, or <see langword="null"/> when nothing went.</returns>
    internal async ValueTask<WithdrawnTurns?> CutUnderSummaryAsync(
        AgentSession opened, string? parentMessageId, CancellationToken cancellationToken)
    {
        var store = _session.Compiled.ConversationStore;

        await _session.History.DrainAsync(opened).ConfigureAwait(false);

        int from;
        if (parentMessageId is null)
        {
            from = 0;
        }
        else if (await store.OrdinalOfAsync(_session.ConversationId, parentMessageId, cancellationToken).ConfigureAwait(false) is { } parent)
        {
            from = parent + 1;
        }
        else
        {
            return null;
        }

        var cut = await store.TruncateAsync(_session.ConversationId, from, cancellationToken).ConfigureAwait(false);
        Log.ConversationTruncated(_session.Logger, _session.ConversationId, from, _session.State.TurnIndex);

        var refreshed = await RecordAsync("edit", cancellationToken).ConfigureAwait(false);
        var rows = await store.ReadForSessionAsync(_session.ConversationId, cancellationToken).ConfigureAwait(false);

        _session.History.Resync(opened, rows, refreshed.NextOrdinal);

        return cut.Turns;
    }

    /// <summary>Store 0's record of this conversation, which must exist for a session that already opened it.</summary>
    /// <param name="purpose">What the session is about to do with it, for the error.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    private async ValueTask<ConversationRecord> RecordAsync(string purpose, CancellationToken cancellationToken)
        => await _session.Compiled.ConversationStore.GetAsync(_session.ConversationId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Store 0 holds no conversation '{_session.ConversationId}' for its own session to {purpose}.");

    /// <summary>
    /// Reads the session of this conversation, or null before its first turn opened one.
    /// </summary>
    /// <returns>The session.</returns>
    internal AgentSession? Session()
    {
        lock (_session.InterruptLock)
        {
            return _session.AgentSession;
        }
    }

    /// <summary>
    /// Waits for every store 1 write this conversation has queued.
    /// </summary>
    /// <returns>A task that completes when the words of the conversation are durable.</returns>
    internal async Task FlushTranscriptAsync()
    {
        if (Session() is { } session)
        {
            await _session.History.DrainAsync(session).ConfigureAwait(false);
        }
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

    /// <summary>The session a graph row's conversation holds, so store 1 has somewhere to live.</summary>
    private sealed class ConversationHistorySession : AgentSession;
}
