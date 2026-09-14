using AgentCore.Application.Calls;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Runtime.Harness;
using AgentCore.Application.Transcript;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime;

// The transcript half of the session (MAF history-provider seam): opening the session once,
// re-syncing it with store 0 before every later turn, draining writes, and naming approval
// answers. Reads and publishes the live session through the owning CallSession.
internal sealed class CallTranscriptLedger
{
    private readonly CallSession _session;

    internal CallTranscriptLedger(CallSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <summary>
    /// Opens the session of this call, once, and re-syncs it with store 0 before handing it to every
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

        var record = await _session.Compiled.CallStore.CreateAsync(_session.CallId, cancellationToken).ConfigureAwait(false);

        CallSessionState? checkpoint;
        lock (_session.InterruptLock)
        {
            checkpoint = _session.Checkpoint;
        }

        // One restore point for both sources, and store 0 outranks the host's checkpoint outright
        // rather than merging with it. Store 0's blob is written in the same batch as the turn's
        // words, so its state and store 1's words are of one moment and cannot disagree; a
        // checkpoint's state beside store 1's words can be of two. So the checkpoint decides only a
        // call store 0 does not know, or knows without state.
        var stored = record.State ?? checkpoint;

        AgentSession session;
        if (_session.SessionCarriesHistory)
        {
            session = stored is { Version: CallSessionState.CurrentVersion, Providers.Count: > 0 }
                ? await _session.Compiled.TurnAgent.DeserializeSessionAsync(
                    HarnessSessionState.Wrap(stored.Providers), cancellationToken: cancellationToken).ConfigureAwait(false)
                : await _session.Compiled.TurnAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            session = new CallHistorySession();

            // Rows 1 and 2 restore provider state onto their long-lived session above. A graph
            // row instead keeps one workflow session for the whole call: MAF restores each
            // participant's provider state from its checkpoints whenever a turn runs on it.
            if (_session.ReusesGraphSession)
            {
                _session.GraphSession = stored is { Version: CallSessionState.CurrentVersion, WorkflowState: { } blob }
                    ? await _session.Compiled.TurnAgent.DeserializeSessionAsync(blob, cancellationToken: cancellationToken).ConfigureAwait(false)
                    : await _session.Compiled.TurnAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        IReadOnlyList<CallMessage> spoken = record.LastMessageAt is null
            ? []
            : await _session.Compiled.CallStore.ReadAsync(_session.CallId, cancellationToken).ConfigureAwait(false);

        lock (_session.InterruptLock)
        {
            if (_session.AgentSession is null)
            {
                _session.AgentSession = session;

                _session.State.TurnIndex = _session.History.BeginCall(
                    session,
                    _session.CallId,
                    spoken,
                    _session.Events.RaiseDroppedTranscriptWrite,
                    new TranscriptMarks(record.NextOrdinal, stored?.NextTurnIndex ?? 0));

                // After the constructor, never inside it. The const writer has already run by now,
                // so a slot a previous session filled lands on top of the const default rather than
                // under it — and the record it is read from only exists after an async store read.
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
    /// only when someone else wrote to the call in between.
    /// </summary>
    /// <param name="opened">The session of this call.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    private async ValueTask ResyncTranscriptAsync(AgentSession opened, CancellationToken cancellationToken)
    {
        await _session.History.DrainAsync(opened).ConfigureAwait(false);

        try
        {
            var refreshed = await _session.Compiled.CallStore.GetAsync(_session.CallId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Store 0 holds no call '{_session.CallId}' for its own session to resync against.");

            if (refreshed.NextOrdinal == _session.History.NextOrdinal(opened))
            {
                return;
            }

            var rows = await _session.Compiled.CallStore.ReadAsync(_session.CallId, cancellationToken).ConfigureAwait(false);

            _session.History.Resync(opened, rows, refreshed.NextOrdinal);
        }
#pragma warning disable CA1031 // A store that cannot be read never ends a call: the turn runs on the words the session holds.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            Log.TranscriptResyncFailed(_session.Logger, _session.CallId, _session.State.TurnIndex, exception);
            _session.Events.RaiseFailedTranscriptResync(_session.State.TurnIndex, exception);
        }
    }

    /// <summary>
    /// Reads the session of this call, or null before its first turn opened one.
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
    /// Waits for every store 1 write this call has queued.
    /// </summary>
    /// <returns>A task that completes when the words of the call are durable.</returns>
    internal async Task FlushTranscriptAsync()
    {
        if (Session() is { } session)
        {
            await _session.History.DrainAsync(session).ConfigureAwait(false);
        }
    }

    /// <summary>Builds the answer message for one queued approval request of this call.</summary>
    /// <param name="requestId">The id the caller answers.</param>
    /// <param name="approved">Whether the tool may run.</param>
    /// <returns>
    /// The user message carrying the approval response, or <see langword="null"/> when this call
    /// queues no request under that id: answered already, another call's, or never asked.
    /// </returns>
    internal ChatMessage? TryCreateApprovalAnswer(string requestId, bool approved)
    {
        ArgumentException.ThrowIfNullOrEmpty(requestId);

        return Session() is { } session
            ? PendingApprovalQueue.AnswerFor(session.StateBag.Serialize(), requestId, approved)
            : null;
    }

    /// <summary>The session a graph row's call holds, so store 1 has somewhere to live.</summary>
    private sealed class CallHistorySession : AgentSession;
}
