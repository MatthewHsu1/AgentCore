using System.Text.Json;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Domain;
using AgentCore.Domain.Audit;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime;

internal sealed class CallTurnCompletion
{
    private readonly CallSession _session;

    internal CallTurnCompletion(CallSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <summary>Runs every writer, then lets the machine pick the stage of the next turn.</summary>
    /// <param name="turn">The turn that just spoke.</param>
    /// <param name="response">What the agent answered.</param>
    /// <param name="toolFault">
    /// The message of the fault that threw out of the run, or <see langword="null"/> when nothing
    /// threw. Section 8.7, row six.
    /// </param>
    /// <param name="cancellationToken">Cancels the extractor call.</param>
    /// <returns>The finished turn.</returns>
    /// <param name="disposition">
    /// What <see cref="ModerationAgent"/> and <see cref="FallbackAgent"/> reported about
    /// this turn, or <see langword="null"/> when neither layer marked it. A flagged turn skips the
    /// extractor: the words moderation flagged are its only input, and a slot filled from them would
    /// carry the flagged content into the state document and into every later prompt. Everything else
    /// about a refused turn is ordinary, so the refusal enters the transcript, the stage machine
    /// advances, and <c>turn.completed</c> proves the refusal under
    /// <see cref="AuditPayloadKeys.ReplyTextSha256"/>.
    /// </param>
    internal async Task<TurnResult> CompleteTurnAsync(
        CallTurn turn,
        AgentResponse response,
        string? toolFault,
        TurnDisposition? disposition,
        CancellationToken cancellationToken)
    {
        // Sweep drain state for tool calls whose round never reached the client's drain: the
        // round that ends a turn attaches nothing, so turn-scoped records would otherwise
        // leave entries sitting on the shared clients.
        turn.Results.Sweep();

        // Calls the model named that no tool answered never reach the invoking client, so no
        // per-call state names them. The turn's own record does: every id the model gave minus
        // every id a tool ran under. Reported here, at the same turn end that sweeps the drain.
        ReportUndeclaredToolCalls(turn, response);

        var interruption = _session.Interruptions.CurrentInterruption();

        // The moderation facts of this turn, raised before the turn's own events so the flag still
        // takes the lower ordinal. The verdict is known before the model runs, which is the one rule
        // that separates prompt.flagged from reply.interrupted: it amends nothing.
        var refused = disposition?.Moderation is ModerationOutcome.Flagged;
        _session.Events.RaiseModeration(turn.Index, disposition);

        // R1 reaches the turn through the fallback layer, and a fault above every chat client — a
        // graph that matched no edge — still reaches the catch in the run methods. Both are the
        // same row-six fact.
        toolFault ??= disposition is { Fallback: FallbackCause.Faulted, FallbackReason: { } caught }
            ? caught
            : null;

        var reply = SpokenReply.From(response.Messages);

        var spokenReply = reply;

        TimeSpan? interruptedAfter = null;

        string? failure = toolFault is null ? null : CallSession.ToolFailureReason + " " + toolFault;
        
        // The tool calls still waiting on a human answer. A pending turn is not a failure: the
        // run suspended instead of answering, so the empty-reply substitution below is skipped,
        // the extractor has nothing decided to read, and the requests ride TurnResult to the host.
        var approvals = ApprovalRequestsOf(response);

        if (interruption is { } cut)
        {
            // Item 6a. The record holds the text the caller heard, not the text the model produced.
            // A trailing space is not speech, so it is trimmed: pipecat pins the same rule in its
            // aggregator test, where the model sent "Hello " and the record holds "Hello".
            reply = cut.HeardText.Trim();
            interruptedAfter = cut.PlayedDuration;
        }
        else if (approvals.Count == 0 && (failure is not null
            || string.IsNullOrWhiteSpace(reply)
            || disposition?.Fallback is FallbackCause.EmptyReply))
        {
            // Section 8.7, last row. A quiet run is silence on a voice call, so an empty reply is a
            // failure even though nothing threw.
            failure ??= CallSession.EmptyReplyReason;
            reply = _session.Compiled.FallbackReply;
            spokenReply = reply;
        }

        // Section 8.7, last row, raised once for the turn. It is diagnostic only, so it takes no
        // ordinal and no row records it; the turn.completed event of this same turn carries the
        // fallback the caller actually heard. The tool fault of row six is raised in
        // WriteTurnEvents instead, because the chain stores that one and its row is stamped with
        // the same endedAt every other event of the turn carries.
        if (toolFault is null && failure is not null)
        {
            _session.Events.RaiseDiagnostic(CallEventKind.EmptyReply, _session.Time.GetUtcNow(), turn.Index);
        }

        // What this turn adds to the transcript. It is built here and written at the end of the
        // method, in one lock with the rest of what a late barge-in may still amend: the transcript
        // span, the ordinal the amendment must reference, and LastTurn itself. Nothing between here
        // and there reads the transcript — the extractor reads the finished turn, and the writers
        // read the state document — so building it now and committing it once costs nothing.
        // A barge-in inside the first 100 ms leaves no heard text, and an empty assistant message
        // teaches the model nothing. pipecat and livekit both guard this. Both row shapes read this
        // one value, so what counts as words the caller heard is decided once.
        var heard = reply.Length > 0 ? new ChatMessage(ChatRole.Assistant, reply) : null;

        // Rows 3 and 4 record the caller-facing turn alone, so `written` stays empty for them: a
        // node's tool pair and a node's line to the next node are neither said nor heard, and the
        // commit below takes the heard reply straight.
        List<ChatMessage> written = [];
        if (_session.SessionCarriesHistory)
        {
            if (interruptedAfter is not null || failure is not null)
            {
                // The transcript holds what the caller heard. A run that stopped mid-round would
                // otherwise leave a tool call with no result behind, and the next turn would send
                // it. So the pairs that finished are kept and only an unpaired call is dropped: a
                // side effect that ran must stay visible to the next turn. livekit/agents fixed the
                // same defect in issue 3702.
                written = [.. TurnMessages.FinishedToolMessages(response.Messages)];

                if (heard is not null)
                {
                    written.Add(heard);
                }
            }
            else
            {
                written = [.. response.Messages];
            }
        }

        // The per-turn writers run from here, in the order the constructor documents: tool results
        // first.
        _session.Writers.ApplyToolResults(response.Messages);

        // The extractor writes next. Its deadline is here and not on the whole method, because the
        // raise of the turn's events and the stage advance must always run.
        string? extractionFailure = null;

        // A refused turn runs no extractor. The words moderation flagged are the extractor's only
        // input, so a slot filled from them would carry the flagged content into the state document
        // and into every later prompt. The refusal itself says nothing worth extracting either, and
        // the extractor costs a model call, so this also spends nothing on a turn nobody answered.
        if (!refused && approvals.Count == 0)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(CallSession.TurnCompletionTimeout);
            try
            {
                extractionFailure = await _session.Writers.ExtractAsync(turn, response, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                extractionFailure = CallSession.ExtractionTimedOutReason;
            }
        }

        if (extractionFailure is not null)
        {
            // Section 8.7, row two: leave the slots unchanged, report once for the turn, and continue
            // the call. State extraction must never drop a call. The reason rides on the event
            // because the line an operator reads is the same one the turn loop used to write itself.
            _session.Events.RaiseDiagnostic(
                CallEventKind.ExtractionFailed,
                _session.Time.GetUtcNow(),
                turn.Index,
                payload: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [CallEventPayloadKeys.Reason] = extractionFailure,
                });
        }

        // The clock fields write next. The clock comes from the injected provider, so a test owns
        // it. The same read stamps every audit event of this turn.
        var endedAt = _session.Time.GetUtcNow();
        _session.State.TurnIndex++;
        _session.State.CallDurationSeconds = (endedAt - _session.StartedAt).TotalSeconds;

        // The counters write last.
        _session.Counters.Apply(_session.State);

        var stageAfter = turn.StageBefore;
        if (_session.Policy is not null)
        {
            stageAfter = _session.Policy.Advance(_session.State.Snapshot());
            _session.State.Stage = stageAfter;
            _session.IsComplete = _session.Policy.IsTerminal;
        }

        TurnResult result = new(
            _session.CallId,
            turn.Index,
            turn.StageBefore,
            stageAfter,
            reply,
            _session.IsComplete,
            extractionFailure,
            failure,
            interruptedAfter,
            endedAt)
        {
            Approvals = approvals,
        };

        // A graph row that reuses its session persists it here, before the commit below reads
        // it into store 0: the next turn resumes from these checkpoints, and a resumed call
        // rebuilds them. Rows 1 and 2 need nothing — their session outlives the call.
        if (_session.ReusesGraphSession && _session.GraphSession is { } shared)
        {
            _session.GraphBlob = await turn.Agent.SerializeSessionAsync(shared, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        // One lock, one moment. A late barge-in reads all four of these together, so a window in
        // which LastTurn already names this turn while the span or the ordinal still names the one
        // before it would let an amendment rewrite the wrong part of the transcript.
        lock (_session.InterruptLock)
        {
            // What the caller said and what the caller heard, written together.
            if (_session.SessionCarriesHistory)
            {
                _session.LastReplyMessageId = _session.History.AppendTurn(
                    turn.Session, [turn.Spoken, .. written], _session.Snapshot(), turn.MessageId);
            }
            else
            {
                // Rows 3 and 4 answer with one reply per node, and the caller hears one of them.
                // Store 1 holds the caller-facing turn alone, so the deliberation that produced the
                // answer never enters the record and never reaches a later turn.
                _session.LastReplyMessageId = _session.History.AppendCallerFacingTurn(
                    turn.Session, turn.Spoken, heard, _session.Snapshot(), turn.MessageId);
            }

            // Only now. The chain stores a hash of the spoken text and store 1 stores the text, so a
            // reply.interrupted raised before the append would name a hash of words nothing holds.
            var completedEventId = _session.Events.WriteTurnEvents(
                turn.Index, endedAt, turn.StageBefore, stageAfter, reply, spokenReply, toolFault, interruptedAfter);

            // A turn one barge-in already cut is not amendable again, so it is not published here.
            _session.AmendableEventId = interruptedAfter is null ? completedEventId : null;
            _session.LastTurn = result;

            // The second read. The first one happened before the extractor await, and that await is
            // bounded only by TurnCompletionTimeout, so a barge-in has five whole seconds in which
            // to land after this turn already decided it was not interrupted. On a streaming turn it
            // finds _runCancellation still set and the run still audible, because EndRun does not
            // run until this method has returned, so it takes the in-flight path, cancels a run that
            // has already stopped, and answers true — and true, on the contract of Interrupt, means
            // the barge-in is recorded. (A turn that never streamed was never audible, so its frame
            // already took the amendment path against the turn before this one and never lands
            // here.) Nothing recorded the streaming frame. Reading _interruption again here, one
            // statement after _amendable and LastTurn were published, hands that frame to the very
            // path a frame arriving one instant later would have taken: the tested amendment, which
            // writes the TurnCompleted then ReplyInterrupted pair T23 asks for. The _amendableEventId
            // guard above already fits: it is published exactly when interruptedAfter is null, which
            // is the only state this window can be reached in.
            if (interruption is null
                && _session.Interruption is { } late
                && _session.Interruptions.AmendLastTurn(late)
                && _session.LastTurn is { } amended)
            {
                // The amendment republished LastTurn, so the turn this method returns and the
                // duration the metric below reads must both come from it and not from the record
                // built before the frame landed.
                result = amended;
                interruptedAfter = amended.InterruptedAfter;
            }

            // Consumed, on whichever path recorded it: the in-flight read at the top of this method
            // fed WriteTurnEvents, and the amendment above wrote its own pair. Nothing downstream
            // may handle one frame twice.
            _session.Interruption = null;

            // The turn is committed, so the run is no longer what the caller is hearing. This is the
            // tail of the same defect the read above fixes: a barge-in landing between this lock and
            // EndRun would otherwise still find the run audible, take the in-flight path, and be
            // recorded nowhere at all. Cleared here, under the lock that just published LastTurn, it
            // reaches AmendLastTurn against this turn instead. EndRun still runs and still owns
            // disposing the source; clearing the field twice costs nothing.
            _session.RunCancellation = null;
            _session.Interruptions.RunIsAudible = false;
        }

        AgentCoreTelemetry.EndTurn(
            turn.Activity,
            _session.Time.GetElapsedTime(turn.StartedAt),
            Outcome(failure, interruptedAfter),
            stageAfter,
            failure);

        if (_session.IsComplete)
        {
            // The machine reached a terminal stage, so the call is over and the chain closes here.
            // The stage rides as detail, because the reason a report counts is the same one for
            // every terminal stage the document declares.
            _session.Events.EndCall(CallEndReason.AgentCompleted, endedAt, stageAfter);
        }

        if (_session.IsComplete || _session.Events.HasEnded)
        {
            // Children before shells: a cancelled child may be inside a shell tool the next line
            // tears down.
            await _session.Lifetime.ReleaseBackgroundSessionsAsync().ConfigureAwait(false);

            // Shells first: a Docker executor's bind mount is this workspace, and it must not be
            // deleted out from under a still-live container.
            await _session.Lifetime.DisposeShellsAsync().ConfigureAwait(false);

            // A tool running mid-turn (a file_memory_write, say) can call EndCall itself, which
            // deletes the workspace and then IsComplete overwrites the flag when the policy also
            // reaches a terminal stage. _events.HasEnded survives that overwrite, so the folder a
            // tool recreated after the mid-turn delete is still cleaned up here.
            _session.Lifetime.DeleteWorkspace();
        }

        return result;
    }

    /// <summary>Reads the closed outcome value of one finished turn.</summary>
    /// <param name="failure">The section 8.7 reason, or <see langword="null"/>.</param>
    /// <param name="interruptedAfter">The played duration, or <see langword="null"/>.</param>
    /// <returns>One of the three values the metric attribute takes.</returns>
    private static string Outcome(string? failure, TimeSpan? interruptedAfter) => (failure, interruptedAfter) switch
    {
        (not null, _) => AgentCoreTelemetry.OutcomeFailed,
        (_, not null) => AgentCoreTelemetry.OutcomeInterrupted,
        _ => AgentCoreTelemetry.OutcomeCompleted,
    };

    /// <summary>Reads the approval requests one finished turn still waits on.</summary>
    /// <param name="response">What the agent answered.</param>
    /// <returns>One entry per request content, oldest first; empty when the turn asked nothing.</returns>
    private static List<PendingApproval> ApprovalRequestsOf(AgentResponse response)
    {
        List<PendingApproval> approvals = [];
        foreach (var request in response.Messages
            .SelectMany(message => message.Contents)
            .OfType<ToolApprovalRequestContent>())
        {
            if (request.ToolCall is not FunctionCallContent call)
            {
                continue;
            }

            approvals.Add(new PendingApproval(
                request.RequestId,
                call.Name,
                JsonSerializer.SerializeToElement(
                    call.Arguments ?? new Dictionary<string, object?>(StringComparer.Ordinal))));
        }

        return approvals;
    }

    /// <summary>Reports calls the model named that no tool answered.</summary>
    /// <param name="turn">The turn that just ran.</param>
    /// <param name="response">What the agent answered.</param>
    private void ReportUndeclaredToolCalls(CallTurn turn, AgentResponse response)
    {
        var invoked = new HashSet<string>(turn.Results.CallIds, StringComparer.Ordinal);

        foreach (var call in response.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>()
            .Where(call => !invoked.Contains(call.CallId)))
        {
            _session.Events.RaiseToolFailure(turn.Index, new ToolFailure
            {
                ToolName = call.Name,
                ToolCallId = call.CallId,
                Kind = ToolFailureKind.Undeclared,
                Message = $"the model called '{call.Name}', and no such tool is declared.",
            });
        }
    }
}
