using AgentCore.Application.Ports;

namespace AgentCore.AspNetCore.Call;

/// <summary>
/// Decides which turn of one call may speak, and which one a barge-in cuts short.
/// </summary>
/// <remarks>
/// <para>
/// The first of the two barge-in gates lives here: once a turn is marked interrupted this class
/// stops handing its fragments to <see cref="ICallOutputPort"/>. The last gate, immediately
/// before the wire, belongs to the port's own implementation and is expressed in that
/// implementation's private vocabulary, so no turn id of this class ever crosses the port.
/// </para>
/// </remarks>
internal sealed class CallTurnArbiter
{
    // Not readonly, and volatile for the same reason the connection's own session field is: a
    // repeated setup frame replaces the call this arbiter runs turns against, and every turn that
    // starts after it must run against the replacement. One arbiter serves the whole connection,
    // across as many sessions as the vendor names, because "one turn at a time" is a property of
    // the transport and not of any one session: a second arbiter would carry its own _turnActive,
    // its own held prompt, and its own ids, and a prompt arriving while the replaced session's turn
    // still ran would start a second turn writing into the same channel.
    private volatile IConversationPort _session;
    private readonly ICallOutputPort _output;
    private readonly ConnectionTaskObserver _observer;
    private readonly Action<string> _logPromptHeld;
    private readonly Action<string> _logPendingPromptDropped;
    private readonly CancellationToken _connectionToken;

    private Task _turn = Task.CompletedTask;
    private bool _loggedPendingPromptDropped;

    // Guards _turnId, _interruptedTurnId, _turnActive, _pendingPrompt, and every place _turn is
    // read then reassigned. The read loop reaches this lock through StartTurnAsync and
    // Interrupt, one frame at a time; a turn's own task reaches it too, from
    // RunPendingPrompt, once it finishes. _turnId and _interruptedTurnId are also read without the
    // lock, through Interlocked.Read, from inside a turn's own hot loop, where taking a lock on
    // every update would slow the path this guard exists to protect.
    private readonly Lock _turnLock = new();
    private long _turnId;

    // The id a barge-in invalidated, or 0 when nothing has been interrupted yet. This is a
    // separate field from _turnId, not a reuse of it, because _turnId also advances on every
    // ordinary turn transition — RunPendingPrompt mints the next id as soon as one turn's own task
    // finishes, which can race ahead of the write loop still sending that turn's own trailing
    // frames. A single shared "current id" field cannot tell "a newer turn has started normally"
    // apart from "this turn was cut short": both look like a mismatch to a check that only asks
    // "does this item's id equal the current one." Recording which id was interrupted, instead of
    // just raising a shared counter, is what lets a turn's own guard ask the question that
    // actually matters: was *this* turn, specifically, the one barge-in cut off, regardless of how
    // many ordinary turns have started since.
    private long _interruptedTurnId;

    // The newest turn whose output this arbiter has actually handed to the output port, or 0 while
    // no turn has produced a word. It is not _turnId, and the difference is the whole of the
    // barge-in target: the transport paces the audio itself, so turn N is still being spoken after
    // its own stream ended, and RunPendingPrompt starts turn N+1 inside turn N's own finally. A
    // barge-in in that window belongs to turn N — the turn the caller was hearing — and marking
    // N+1 instead would drop every update of a turn nobody has heard yet and skip its closing
    // last: true frame, leaving the caller with dead air and the vendor with an unfinished reply.
    private long _spokenTurnId;
    private bool _turnActive;
    private string? _pendingPrompt;

    /// <summary>Builds the arbiter one call runs its turns through.</summary>
    /// <param name="session">The call this arbiter runs turns against, until <see cref="Rebind"/> names another.</param>
    /// <param name="output">Where a turn's reply goes, one fragment at a time.</param>
    /// <param name="observer">Watches a turn's task, and never lets its fault go unobserved.</param>
    /// <param name="logPromptHeld">
    /// Logs that one prompt was held until the turn in flight ends, given the id of the call it was
    /// held for. Called once per held prompt, from inside the turn lock.
    /// </param>
    /// <param name="logPendingPromptDropped">
    /// Logs that a further prompt was dropped because one was already held, given the id of the call
    /// it was dropped for. Called at most once per connection, from inside the turn lock.
    /// </param>
    /// <param name="connectionToken">
    /// Cancelled once, for any reason the transport is going away. Every turn reads it, and no
    /// held prompt starts a turn after it fires.
    /// </param>
    public CallTurnArbiter(
        IConversationPort session,
        ICallOutputPort output,
        ConnectionTaskObserver observer,
        Action<string> logPromptHeld,
        Action<string> logPendingPromptDropped,
        CancellationToken connectionToken)
    {
        _session = session;
        _output = output;
        _observer = observer;
        _logPromptHeld = logPromptHeld;
        _logPendingPromptDropped = logPendingPromptDropped;
        _connectionToken = connectionToken;
    }

    /// <summary>Gets the turn running now, or the one that ran last.</summary>
    /// <remarks>
    /// Read under the same lock every writer takes. <see cref="RunPendingPrompt"/> can reassign the
    /// turn from a turn's own task, off the read loop, so teardown is not the only writer's own
    /// reader: without the lock it could observe a stale reference and let the real last turn
    /// finish unobserved. <see cref="RunPendingPrompt"/> itself checks the connection token before
    /// it will start a turn from a held prompt, which is what stops the reverse case — a fresh turn
    /// appearing here after teardown already read it.
    /// </remarks>
    public Task CurrentTurn
    {
        get
        {
            lock (_turnLock)
            {
                return _turn;
            }
        }
    }

    /// <summary>Points this arbiter at another call, keeping every turn it is already running.</summary>
    /// <param name="session">The call every turn started from now on runs against.</param>
    /// <remarks>
    /// For the one caller that needs it: a transport whose vendor names the call a second time on
    /// one connection. Only the session moves. <c>_turnActive</c>, the held prompt, the ids, and the
    /// log-once flag all stay exactly where they are, because "one turn at a time" and "one held
    /// prompt, logged once" are promises this connection makes for its whole life and not for one
    /// session of it. A turn already in flight keeps the session it started on, the same way it
    /// would have before this method existed.
    /// </remarks>
    public void Rebind(IConversationPort session) => _session = session;

    /// <summary>Starts a turn for what the caller just said, or holds it until the turn in flight ends.</summary>
    /// <param name="text">What the caller said.</param>
    /// <returns>
    /// The turn this call started, or a completed task when the prompt was held or dropped. It
    /// reports only completion: a turn's own fault stays on <see cref="CurrentTurn"/>, which the
    /// next turn and teardown both already observe through <see cref="ConnectionTaskObserver"/>.
    /// </returns>
    public Task StartTurnAsync(string text)
    {
        // Read once, here, so the turn this call starts and the line it may log about holding the
        // prompt both name the call the setup frame most recently gave this connection.
        var session = _session;

        Task justFinished;
        lock (_turnLock)
        {
            if (_turnActive)
            {
                // The caller finished a second sentence before the agent spoke. There is no
                // interrupt frame for this, because the vendor never truncated anything, so there
                // is no heard text to record. IConversationPort throws on a second turn of one
                // call, and a throw here would drop it, so one prompt is held and runs once the
                // turn in flight ends. Item 6a of section 11 draws the line at one: a queue of
                // held prompts would answer questions the caller has already moved past, so a
                // third prompt is dropped instead of queued behind the first.
                //
                // _turnActive, not _turn.IsCompleted: RunPendingPrompt runs inside RunTurnAsync's
                // own finally, strictly before that turn's Task itself transitions to completed, so
                // a check against IsCompleted here could see "no turn running" for a call whose own
                // RunPendingPrompt had already run and found nothing pending — losing this prompt
                // outright, since nothing would ever come back to run it. _turnActive is cleared by
                // RunPendingPrompt in that same lock acquisition instead, so this check and that
                // clear can never miss each other.
                if (_pendingPrompt is null)
                {
                    _pendingPrompt = text;
                    _logPromptHeld(session.CallId);
                }
                else if (!_loggedPendingPromptDropped)
                {
                    // Log once for the call, not once for the frame. Same rule as the
                    // unknown-frame line.
                    _loggedPendingPromptDropped = true;
                    _logPendingPromptDropped(session.CallId);
                }

                return Task.CompletedTask;
            }

            justFinished = _turn;
            _turnActive = true;
        }

        return StartAfterAsync(session, text, justFinished);
    }

    /// <summary>Ends the running turn where the caller cut the reply off, and stops the audio behind it.</summary>
    /// <param name="heardText">The text the caller actually heard, as the transport reported it.</param>
    /// <param name="playedDuration">How much of the reply played, as the transport reported it.</param>
    /// <returns>
    /// <see langword="true"/> when the call recorded the barge-in, against the running turn or
    /// against the turn that finished last, and <see langword="false"/> when there was nothing to
    /// record it against.
    /// </returns>
    /// <remarks>
    /// Synchronous, and it stays synchronous. It marks the interrupted id, decides
    /// <c>cutsRunningTurn</c>, and calls <see cref="IConversationPort.Interrupt"/> before anything
    /// awaits, and it hands <see cref="ICallOutputPort.StopAsync"/>'s task to the observer rather
    /// than awaiting it: this runs on the read loop, and a read loop that blocks cannot receive the
    /// next frame.
    /// </remarks>
    public bool Interrupt(string heardText, TimeSpan playedDuration)
    {
        // Read once, here, so the record goes to the call the setup frame most recently named — the
        // same field, read at the same point, as when this method still lived on the connection.
        var session = _session;

        bool cutsRunningTurn;
        lock (_turnLock)
        {
            // Mark before cancelling. Every project that solves this race does it in this order:
            // a token already past the guard in RunTurnAsync must find its own id already marked
            // interrupted right here, not a cancellation that is still on its way to the model
            // call.
            //
            // _spokenTurnId is what is marked, and never _turnId. The two differ exactly when a
            // held prompt fired: turn N finishes streaming, RunPendingPrompt starts turn N+1 inside
            // turn N's own finally, and the transport is still speaking turn N. _turnId then names
            // N+1, a turn the caller has not heard one word of, and marking it would silence it and
            // skip its closing frame. _spokenTurnId names the turn whose output actually reached
            // the transport, which is the turn the caller was hearing and the only one a barge-in
            // may cut.
            //
            // Zero means no turn has produced a word yet, so there is nothing to cut short and the
            // mark stays where it was.
            var spoken = Interlocked.Read(ref _spokenTurnId);
            if (spoken != 0)
            {
                Interlocked.Exchange(ref _interruptedTurnId, spoken);
            }

            // The same comparison the mark above rests on, carried across the seam. CallSession
            // cannot reach it: it decides from whether the *running* turn is audible to it, and a
            // turn is audible to it the moment it hands over any content at all — a tool call, a
            // line of reasoning — none of which is a word the transport ever spoke. So turn N+1 can
            // be audible to the core while _spokenTurnId still names turn N, which is the turn the
            // transport is still playing and the only one the caller has heard. Deciding here, where
            // both ids are known and already under the lock that guards them, is what puts the heard
            // text on turn N instead of on a turn nobody has heard one word of.
            //
            // Zero answers false as well. Nothing of this call has reached the transport, so there
            // is no running turn the caller was hearing, and cutting one short would destroy a reply
            // against nothing — the same rule the mark above already follows by staying put.
            cutsRunningTurn = spoken != 0 && spoken == Interlocked.Read(ref _turnId);
        }

        // The transport measured both values itself. D28 and item 6a: nothing here estimates either
        // one. A false result means there was no turn to record the barge-in against — not merely
        // that none was running, because a turn that already ended is amended rather than ignored.
        var recorded = session.Interrupt(heardText, playedDuration, cutsRunningTurn);

        // The transport already stopped its own audio, and anything the port still holds is the
        // only audio left that could start it playing again. Handed to the observer rather than
        // awaited: this runs on the read loop, and a read loop that blocks cannot receive the next
        // frame. StopAsync itself is called here, before the discard, so whatever it does
        // synchronously — dropping what has not been spoken — has already happened by the time this
        // method returns.
        _ = _observer.ObserveAsync(_output.StopAsync(_connectionToken).AsTask(), ConnectionTaskKind.WriteLoop);

        return recorded;
    }

    /// <summary>Observes the turn that just ended, then starts the next one and follows it to its end.</summary>
    /// <param name="session">The call the turn this starts runs against.</param>
    /// <param name="text">What the caller said.</param>
    /// <param name="justFinished">The turn this call read out of <see cref="CurrentTurn"/>.</param>
    /// <returns>A task that completes when the turn this call starts completes.</returns>
    private async Task StartAfterAsync(IConversationPort session, string text, Task justFinished)
    {
        // A call runs many turns, and CurrentTurn only ever holds the most recent one. Observing the
        // finished turn here is what stops a turn that faulted mid-call from being silently
        // replaced by the next one before anything looks at it. The in-flight guard above only
        // proved _turnActive was false, not that justFinished itself has completed: RunPendingPrompt
        // clears _turnActive from inside RunTurnAsync's own finally, strictly before that turn's
        // Task transitions to completed, so a moment can exist where _turnActive already reads false
        // while justFinished is still finishing the last step of its own async state machine. This
        // await can therefore still yield here, but only for as long as that tail takes to unwind —
        // microseconds — which is why it is safe to await inline rather than fire-and-forget. Only
        // the very last turn of the call still relies on teardown's own observation.
        await _observer.ObserveAsync(justFinished, ConnectionTaskKind.Turn).ConfigureAwait(false);

        Task started;
        lock (_turnLock)
        {
            // Teardown may have completed while the previous turn unwound; a turn started now
            // would outlive the connection's own wait.
            if (_connectionToken.IsCancellationRequested)
            {
                _turnActive = false;
                return;
            }

            var turnId = Interlocked.Increment(ref _turnId);
            started = _turn = RunTurnAsync(session, text, turnId);
        }

        try
        {
            await started.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Swallowed here on purpose, and only here. The task this method returns exists so a
            // caller can follow the turn to its end; the fault itself stays on CurrentTurn, which
            // the next turn's own observation above and teardown's both already report through
            // ConnectionTaskObserver. Reporting it a second time here would log one fault twice.
        }
    }

    /// <summary>Runs one turn end to end, and streams its reply to the output port.</summary>
    /// <param name="session">
    /// The call this turn runs against. Passed in rather than read off the field, so a turn already
    /// in flight when <see cref="Rebind"/> names another call still finishes on the one it started on.
    /// </param>
    /// <param name="text">What the caller said.</param>
    /// <param name="turnId">The id this turn's output is marked with.</param>
    /// <returns>A task that completes when the turn has ended, however it ended.</returns>
    private async Task RunTurnAsync(IConversationPort session, string text, long turnId)
    {
        try
        {
            await foreach (var update in session
                .RunTurnStreamingAsync(text, _connectionToken)
                .ConfigureAwait(false))
            {
                // This check narrows the race; it does not close it. It sits after the await on the
                // enumerator and before the await on the output port, so it stops every update the
                // model produces *after* Interrupt marks this turn interrupted. It cannot stop
                // an update already past this line at that moment — already queued inside the port,
                // or parked inside SpeakAsync waiting for room — so the port's own last gate,
                // immediately before the wire, is what actually keeps a straggler off the wire.
                // That gate reads the port's own reply generation rather than this id, because no
                // turn id of this class may cross the port; StopAsync raises the generation, and
                // every fragment already queued under the old one is dropped there. The check is
                // against _interruptedTurnId, not _turnId: a later, ordinary turn starting also
                // moves _turnId, and that alone must not silence a turn nothing actually
                // interrupted.
                //
                // continue, not return: abandoning the enumeration here would dispose
                // CallSession's own iterator from the outside, which unwinds it past the code that
                // records the interruption on LastTurn rather than through it. Session.Interrupt
                // already asked the model call to stop; this loop still has to drain whatever
                // update that call already had in flight so CallSession reaches its own ending on
                // its own terms. Every update after the raised id is read and thrown away instead
                // of queued, which is what keeps the queue itself from growing on a barge-in.
                if (Interlocked.Read(ref _interruptedTurnId) == turnId)
                {
                    continue;
                }

                if (update.Text is { Length: > 0 } piece)
                {
                    // Marked before the write, not after it. Once a piece is committed to the
                    // port the caller is going to hear it, and a barge-in that lands while this
                    // write waits on backpressure still belongs to this turn. The ids only ever
                    // increase here, because RunPendingPrompt starts the next turn from inside this
                    // turn's own finally, so a plain exchange cannot move this value backwards.
                    Interlocked.Exchange(ref _spokenTurnId, turnId);

                    await _output.SpeakAsync(piece, _connectionToken).ConfigureAwait(false);
                }
            }

            if (Interlocked.Read(ref _interruptedTurnId) != turnId)
            {
                // The vendor closes a reply on last: true, and the sample uses an empty final
                // token when the stream ended with no trailing text. A barge-in already ended
                // this turn, so a closing frame now would speak a reply the caller cut off.
                await _output.CompleteAsync(_connectionToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // CallSession absorbs a barge-in's own cancellation internally and ends the turn
            // normally, so what reaches here is the connection's own teardown token instead. The
            // call itself goes on; only this turn stops.
        }
        finally
        {
            RunPendingPrompt();
        }
    }

    /// <summary>Starts the one prompt <see cref="StartTurnAsync"/> held while this turn ran.</summary>
    /// <remarks>
    /// Called from a turn's own <c>finally</c>, so it runs on whichever thread that turn happens
    /// to finish on — never the read loop, and strictly before the Task <see cref="RunTurnAsync"/>
    /// returns actually completes. Reading <see cref="_pendingPrompt"/>, clearing <see
    /// cref="_turnActive"/> or handing it to a new turn, and reassigning <see cref="_turn"/> all
    /// happen inside one lock acquisition, because a caller of <see cref="StartTurnAsync"/> that
    /// reads <c>_turnActive</c> as false must also see <see cref="_pendingPrompt"/> as null in that
    /// same instant — otherwise both this method and that caller could start a turn at once.
    /// </remarks>
    private void RunPendingPrompt()
    {
        lock (_turnLock)
        {
            // The connection is tearing down, so a fresh turn here would speak after the transport
            // stopped reading, and teardown's own read of CurrentTurn (also under this lock) may
            // already have captured the task this call is inside of — leaving a turn nothing waits
            // for. The caller's last sentence goes unanswered either way once the socket is gone.
            if (_pendingPrompt is not { } held || _connectionToken.IsCancellationRequested)
            {
                _turnActive = false;
                return;
            }

            _pendingPrompt = null;
            var turnId = Interlocked.Increment(ref _turnId);

            // Read here and not captured when the prompt was held: a setup frame that named another
            // call in between makes that call the one to answer, which is the rule the connection
            // followed when this method still lived there.
            _turn = RunTurnAsync(_session, held, turnId);
        }
    }
}
