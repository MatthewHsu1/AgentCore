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
    private readonly CallTurnCommit _commit;

    internal CallTurnCompletion(CallSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _commit = new CallTurnCommit(session);
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

        var outcome = ResolveReply(turn, response, interruption, disposition, toolFault);
        var addition = TranscriptAddition(response, outcome);

        // The per-turn writers run from here, in the order the constructor documents: tool results
        // first.
        _session.Writers.ApplyToolResults(response.Messages);

        // A refused turn runs no extractor. The words moderation flagged are the extractor's only
        // input, so a slot filled from them would carry the flagged content into the state document
        // and into every later prompt. The refusal itself says nothing worth extracting either, and
        // the extractor costs a model call, so this also spends nothing on a turn nobody answered.
        // A pending turn runs none either: the run suspended instead of answering, so there is
        // nothing decided to read.
        var extractionFailure = refused || outcome.Approvals.Count > 0
            ? null
            : await ExtractAsync(turn, response, cancellationToken).ConfigureAwait(false);

        // The clock fields write next. The clock comes from the injected provider, so a test owns
        // it. The same read stamps every audit event of this turn.
        var endedAt = _session.Time.GetUtcNow();
        _session.State.TurnIndex++;
        _session.State.CallDurationSeconds = (endedAt - _session.StartedAt).TotalSeconds;

        // The counters write last.
        _session.Counters.Apply(_session.State);

        var stageAfter = AdvanceStage(turn);

        TurnResult result = new(
            _session.CallId,
            turn.Index,
            turn.StageBefore,
            stageAfter,
            outcome.Reply,
            _session.IsComplete,
            extractionFailure,
            outcome.Failure,
            outcome.InterruptedAfter,
            endedAt)
        {
            Approvals = outcome.Approvals,
        };

        // A graph row that reuses its session persists it here, before the commit below reads
        // it into store 0: the next turn resumes from these checkpoints, and a resumed call
        // rebuilds them. Rows 1 and 2 need nothing — their session outlives the call.
        if (_session.ReusesGraphSession && _session.GraphSession is { } shared)
        {
            _session.GraphBlob = await turn.Agent.SerializeSessionAsync(shared, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        result = _commit.Publish(turn, outcome, addition, result, interruption);

        AgentCoreTelemetry.EndTurn(
            turn.Activity,
            _session.Time.GetElapsedTime(turn.StartedAt),
            Outcome(result.Failure, result.InterruptedAfter),
            stageAfter,
            result.Failure);

        await _commit.EndCallIfOverAsync(endedAt, stageAfter).ConfigureAwait(false);

        return result;
    }

    private ReplyOutcome ResolveReply(
        CallTurn turn,
        AgentResponse response,
        CallInterruptionTracker.Interruption? interruption,
        TurnDisposition? disposition,
        string? toolFault)
    {
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

        return new ReplyOutcome(reply, spokenReply, failure, toolFault, interruptedAfter, approvals);
    }

    // Built before the writers run and committed at the end of the turn, in one lock with the rest
    // of what a late barge-in may still amend: the transcript span, the ordinal the amendment must
    // reference, and LastTurn itself. Nothing in between reads the transcript — the extractor reads
    // the finished turn, and the writers read the state document — so building it early and
    // committing it once costs nothing.
    private TranscriptEntry TranscriptAddition(AgentResponse response, ReplyOutcome outcome)
    {
        // A barge-in inside the first 100 ms leaves no heard text, and an empty assistant message
        // teaches the model nothing. pipecat and livekit both guard this. Both row shapes read this
        // one value, so what counts as words the caller heard is decided once.
        var heard = outcome.Reply.Length > 0 ? new ChatMessage(ChatRole.Assistant, outcome.Reply) : null;

        // Rows 3 and 4 record the caller-facing turn alone, so `written` stays empty for them: a
        // node's tool pair and a node's line to the next node are neither said nor heard, and the
        // commit takes the heard reply straight.
        if (!_session.SessionCarriesHistory)
        {
            return new TranscriptEntry(heard, []);
        }

        if (outcome.InterruptedAfter is null && outcome.Failure is null)
        {
            return new TranscriptEntry(heard, [.. response.Messages]);
        }

        // The transcript holds what the caller heard. A run that stopped mid-round would
        // otherwise leave a tool call with no result behind, and the next turn would send
        // it. So the pairs that finished are kept and only an unpaired call is dropped: a
        // side effect that ran must stay visible to the next turn. livekit/agents fixed the
        // same defect in issue 3702.
        List<ChatMessage> written = [.. TurnMessages.FinishedToolMessages(response.Messages)];
        if (heard is not null)
        {
            written.Add(heard);
        }

        return new TranscriptEntry(heard, written);
    }

    // The extractor's deadline is here and not on the whole turn, because the raise of the turn's
    // events and the stage advance must always run.
    private async Task<string?> ExtractAsync(CallTurn turn, AgentResponse response, CancellationToken cancellationToken)
    {
        string? extractionFailure;

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

        return extractionFailure;
    }

    private string AdvanceStage(CallTurn turn)
    {
        if (_session.Policy is null)
        {
            return turn.StageBefore;
        }

        var stageAfter = _session.Policy.Advance(_session.State.Snapshot());
        _session.State.Stage = stageAfter;
        _session.IsComplete = _session.Policy.IsTerminal;
        return stageAfter;
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
