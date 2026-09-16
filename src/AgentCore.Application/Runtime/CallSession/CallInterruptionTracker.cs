namespace AgentCore.Application.Runtime;

internal sealed class CallInterruptionTracker
{
    private readonly CallSession _session;

    // One session runs one turn at a time. Lives here, with the rest of the live run, so the
    // guard and the release travel together; BeginTurn enters through TryEnterTurn.
    private int _running;

    // Whether the running turn has already handed the host something to speak. One rule for both
    // run shapes: a run that has handed the host nothing cannot be the turn the caller was hearing.
    private volatile bool _runIsAudible;

    /// <summary>
    /// Whether the running turn has handed the host something to speak.
    /// </summary>
    internal bool RunIsAudible { get => _runIsAudible; set => _runIsAudible = value; }

    internal CallInterruptionTracker(CallSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <summary>
    /// Ends the running turn where the caller cut the reply off.
    /// </summary>
    /// <param name="utteranceUntilInterrupt">The text the caller actually heard.</param>
    /// <param name="durationUntilInterrupt">How much of the reply played, as the relay reported it.</param>
    /// <param name="cutsRunningTurn">
    /// Whether the turn running now is the turn the caller was hearing. An adapter that tracks which
    /// turn's output actually reached the vendor answers that question itself and says so here;
    /// <see langword="false"/> sends the frame straight to the amendment path, whatever the running
    /// turn has produced. The default is <see langword="true"/>, which leaves the decision to this
    /// session alone, and that is the right answer for a caller that speaks over a reply it is
    /// itself streaming.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when this call recorded the barge-in, either by ending the running
    /// turn or by amending the turn that finished last, and <see langword="false"/> when there was
    /// nothing to record it against.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="durationUntilInterrupt"/> is negative.
    /// </exception>
    internal bool Interrupt(
        string utteranceUntilInterrupt,
        TimeSpan durationUntilInterrupt,
        bool cutsRunningTurn = true)
    {
        ArgumentNullException.ThrowIfNull(utteranceUntilInterrupt);
        ArgumentOutOfRangeException.ThrowIfLessThan(durationUntilInterrupt, TimeSpan.Zero);

        lock (_session.InterruptLock)
        {
            // Both conditions, and in this order. The adapter's answer only ever removes the
            // in-flight path: an adapter that knows the caller was hearing an earlier turn skips it
            // outright, and one that says nothing leaves this session's own audibility test to
            // decide.
            if (cutsRunningTurn && _session.RunCancellation is { } cancellation && RunIsAudible)
            {
                _session.Interruption = new Interruption(utteranceUntilInterrupt, durationUntilInterrupt);
                cancellation.Cancel();
                return true;
            }

            return AmendLastTurn(new Interruption(utteranceUntilInterrupt, durationUntilInterrupt));
        }
    }

    /// <summary>
    /// Opens the window in which <see cref="Interrupt"/> reaches this turn.
    /// </summary>
    /// <param name="cancellationToken">The token of the host.</param>
    /// <returns>The source the run reads. The caller ends it with <see cref="EndRun"/>.</returns>
    internal CancellationTokenSource StartRun(CancellationToken cancellationToken)
    {
        CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        lock (_session.InterruptLock)
        {
            _session.Interruption = null;
            _session.RunCancellation = cancellation;
            _runIsAudible = false;
        }

        return cancellation;
    }

    /// <summary>Takes the turn slot, unless a turn of this call is still running.</summary>
    /// <returns><see langword="false"/> when another turn holds the slot.</returns>
    internal bool TryEnterTurn() => Interlocked.Exchange(ref _running, 1) == 0;

    /// <summary>Frees the turn slot after a turn refused to start left it taken.</summary>
    internal void ReleaseTurn() => Volatile.Write(ref _running, 0);

    /// <summary>
    /// Closes the window, and frees the session for the next turn.
    /// </summary>
    /// <param name="cancellation">The source <see cref="StartRun"/> returned.</param>
    internal void EndRun(CancellationTokenSource cancellation)
    {
        // The field drops first. A frame that arrives now meets no source to cancel, so it reaches
        // the amendment path rather than a disposed one.
        lock (_session.InterruptLock)
        {
            _session.RunCancellation = null;
            _runIsAudible = false;
        }

        cancellation.Dispose();
        Volatile.Write(ref _running, 0);
    }

    /// <summary>
    /// Records a barge-in against the turn that finished last, and corrects its record.
    /// </summary>
    /// <param name="cut">What the relay reported: the heard text and the played duration.</param>
    /// <returns>
    /// <see langword="true"/> when the turn was amended, and <see langword="false"/> when there was
    /// no turn to amend.
    /// </returns>
    internal bool AmendLastTurn(Interruption cut)
    {
        // The chain has already closed, so nothing may be appended behind call.ended.
        if (_session.Events.HasEnded
            || _session.AmendableEventId is not { } completedEventId
            || _session.LastTurn is not { InterruptedAfter: null } finished
            || _session.Ledger.Session() is not { } session)
        {
            return false;
        }

        var heard = cut.HeardText.Trim();

        // Store 1 keeps the reply row and rewrites its words, rather than replacing the turn's
        // messages. The turn it corrects ran to its end, so every tool call it made is already
        // paired and there is nothing unfinished to strip out.
        //
        // The cut always lands today: a turn is amendable only when it completed with words, and a
        // turn that completed with words left a reply row holding them. The answer is still read,
        // because the row below names a HASH of those words — a chain that proves words store 1
        // does not hold proves nothing, so no cut means no amendment.
        if (!_session.History.TruncateLastReply(session, heard, cut.PlayedDuration))
        {
            return false;
        }

        _session.LastTurn = finished with { ReplyText = heard, InterruptedAfter = cut.PlayedDuration };

        _session.Events.RaiseReplyInterrupted(
            finished.TurnIndex, _session.Time.GetUtcNow(), completedEventId, heard, cut.PlayedDuration);

        return true;
    }

    /// <summary>Reads the interruption the relay reported for the running turn.</summary>
    /// <returns>The record, or <see langword="null"/> when the caller did not interrupt.</returns>
    internal Interruption? CurrentInterruption()
    {
        lock (_session.InterruptLock)
        {
            return _session.Interruption;
        }
    }

    /// <summary>What the relay reported when the caller spoke over the reply.</summary>
    /// <param name="HeardText">The text the caller actually heard.</param>
    /// <param name="PlayedDuration">How much of the reply played, at 1 ms.</param>
    internal sealed record Interruption(string HeardText, TimeSpan PlayedDuration);
}
