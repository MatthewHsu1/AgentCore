using System.Diagnostics;
using System.Globalization;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Hooks.BuiltIn;
using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Transcript;
using AgentCore.Domain;
using AgentCore.Domain.Audit;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentCore.Application.Runtime.Agents;
using AgentCore.Application.Runtime.Cut;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Runtime.Turn.Lifecycle
{
    /// <summary>
    /// The conversation's side of one turn's seal: it decides the turn after its run, and publishes it once
    /// <see cref="ConversationTurnAgent"/> committed the words. One instance serves one turn.
    /// </summary>
    internal sealed class ConversationTurnCompletion : ITurnCompleter
    {
        private readonly ConversationSession _session;

        private readonly ConversationTurn _turn;

        private readonly ConversationTurnCommit _commit;

        private ReplyOutcome? _outcome;

        private TurnResult? _decided;

        private TurnCut? _cut;

        private TurnDisposition? _disposition;

        private bool _escaped;

        /// <summary>Creates the completion of one turn.</summary>
        /// <param name="session">The conversation.</param>
        /// <param name="turn">The turn it completes.</param>
        internal ConversationTurnCompletion(ConversationSession session, ConversationTurn turn)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(turn);
            _session = session;
            _turn = turn;
            _commit = new ConversationTurnCommit(session);
        }

        /// <summary>Gets the finished turn, or <see langword="null"/> until the turn is published.</summary>
        internal TurnResult? Result { get; private set; }

        /// <inheritdoc />
        public void Superseded(WithdrawnTurns withdrawn)
        {
            _session.Clarifications.Withdraw();

            SessionHooks hooks = _session.Hooks;
            _ = hooks.Raise(new TurnSuperseded(hooks.Scope(_session.State.TurnIndex, stage: null), withdrawn.First, withdrawn.Last));
        }

        /// <summary>Runs every writer, then lets the machine pick the stage of the next turn.</summary>
        /// <param name="sealing">
        /// The turn as the layer saw it end. Its disposition is what <see cref="ModerationAgent"/> and
        /// <see cref="FallbackAgent"/> reported. A flagged turn skips the
        /// extractor: the words moderation flagged are its only input, and a slot filled from them would
        /// carry the flagged content into the state document and into every later prompt. Everything else
        /// about a refused turn is ordinary, so the refusal enters the transcript, the stage machine
        /// advances, and <c>turn.completed</c> proves the refusal under
        /// <see cref="AuditPayloadKeys.ReplyTextSha256"/>.
        /// </param>
        /// <param name="fault">
        /// The fault that threw out of the run, or <see langword="null"/> when nothing threw.
        /// </param>
        /// <returns>The commit, with the state after the stage advance and the line the record holds.</returns>
        public async ValueTask<TurnCommit> CompleteAsync(TurnCommit sealing, Exception? fault)
        {
            ArgumentNullException.ThrowIfNull(sealing);

            Activity.Current = _turn.Activity;

            ConversationTurn turn = _turn;

            AgentResponse response = sealing.Seen ?? new AgentResponse();

            TurnDisposition? disposition = sealing.Disposition;

            _disposition = disposition;
            _escaped = fault is not null;

            _cut = sealing.Cut;

            turn.Results.Sweep();

            ReportUndeclaredToolCalls(turn, response);

            bool refused = disposition?.Moderation is ModerationOutcome.Flagged || disposition?.Blocked == true;

            ReplyOutcome outcome = TurnReplyResolution.Resolve(_session, turn, response, _cut, disposition, fault, sealing.CallerFacing);
            _session.Writers.ApplyToolResults(response.Messages);

            string? extractionFailure = refused || outcome.Approvals.Count > 0
                ? null
                : await ExtractAsync(turn, response).ConfigureAwait(false);

            DateTimeOffset endedAt = _session.Time.GetUtcNow();

            _session.State.TurnIndex++;
            _session.State.ConversationDurationSeconds = (endedAt - _session.StartedAt).TotalSeconds;

            _ = _session.Counters.Apply(_session.State);

            string stageAfter = AdvanceStage(turn);

            _decided = new(
                _session.ConversationId,
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
            _outcome = outcome;

            if (_session.ReusesGraphSession && _session.GraphSession is { } shared)
            {
                _session.GraphBlob = await turn.Agent.SerializeSessionAsync(shared).ConfigureAwait(false);
            }

            return sealing with
            {
                Completed = outcome.InterruptedAfter is null && outcome.Failure is null,
                Reply = outcome.Reply,
                State = _session.States.Snapshot(),
                Unasked = [.. turn.Results.DeniedCallIds, .. turn.Waiting, .. _session.Hooks.ApprovalDecisions.Where(static open => open.Value.Approved is false).Select(static open => open.Key)],
            };
        }

        /// <inheritdoc />
        public async ValueTask<bool> RefusedAsync(Task<bool> refused)
        {
            if (!await _commit.RefusedAsync(refused).ConfigureAwait(false))
            {
                return false;
            }

            TurnRefusals.Raise(_session, _turn.Index, TurnRefusal.Conflict);

            // A refused turn raises no TurnCompleted, so the hooks never see the failure the run ended on.
            if (_outcome is { IsToolFault: false } refusedOutcome && (refusedOutcome.Fault is not null || refusedOutcome.Failure is not null))
            {
                TurnFailureAccounting.Account(
                    _session.Logger,
                    _session.ConversationId,
                    _turn.Index,
                    refusedOutcome.Fault is null ? TurnOutcome.Empty : TurnOutcome.Faulted,
                    failedInTool: false,
                    refusedOutcome.Fault);
            }

            AgentCoreTelemetry.EndTurn(
                _turn.Activity,
                _session.Time.GetElapsedTime(_turn.StartedAt),
                AgentCoreTelemetry.OutcomeFailed,
                _turn.StageBefore,
                ConversationTurnCommit.RefusedReason);

            return true;
        }

        /// <inheritdoc />
        public void Committed((string UserMessageId, string? ReplyMessageId)? ids, string spoken, TurnCut? late, TurnCut? recut)
        {
            if (_outcome is not { } outcome || _decided is not { } decided)
            {
                throw new InvalidOperationException("The turn was committed before it was completed.");
            }

            // A barge-in after the seal amends the reply just written, unless a cut already shaped it; a recut
            // amends the reply that cut shaped.
            Result = _commit.Publish(ids, spoken, decided, _cut, _cut is null ? late : recut, Completed(spoken, outcome, decided));
        }

        private TurnCompleted Completed(string spoken, ReplyOutcome outcome, TurnResult decided)
        {
            SessionHooks hooks = _session.Hooks;
            return new TurnCompleted(
                hooks.Scope(decided.TurnIndex, decided.StageBefore),
                TurnOutcomes.Of(outcome, _disposition, _cut, _escaped),
                _turn.Spoken.Text,
                _cut is null ? spoken : outcome.GeneratedText,
                decided.StageBefore,
                decided.StageAfter,
                _session.Time.GetElapsedTime(_turn.StartedAt),
                decided.Failure,
                outcome.IsToolFault,
                outcome.Fault);
        }

        /// <inheritdoc />
        public async ValueTask FinishAsync()
        {
            if (Result is not { } result)
            {
                throw new InvalidOperationException("The turn was finished before it was committed.");
            }

            AgentCoreTelemetry.EndTurn(
                _turn.Activity,
                _session.Time.GetElapsedTime(_turn.StartedAt),
                Outcome(result.Failure, result.Cut),
                result.StageAfter,
                result.Failure,
                ErrorType(result.Failure));

            await _commit.EndConversationIfOverAsync(result.StageAfter, result.TurnIndex).ConfigureAwait(false);
        }

        /// <summary>Reads the <c>error.type</c> value a failed turn's span carries. The metric point keeps its outcome alone.</summary>
        /// <param name="failure">The reason the record itself holds, or <see langword="null"/> when the turn answered.</param>
        /// <returns>
        /// The exception's own type when <see cref="TurnReplyResolution.Resolve"/> caught one, the closed
        /// empty-reply token when it did not, or <see langword="null"/> when the turn did not fail. Never
        /// the exception's message: that can carry caller or model text, and <paramref name="failure"/>
        /// already holds it for whoever reads <see cref="TurnResult"/> itself.
        /// </returns>
        private string? ErrorType(string? failure)
        {
            return (failure, _outcome?.Fault) switch
            {
                (null, _) => null,
                (_, { } fault) => fault.GetType().FullName,
                _ => AgentCoreTelemetry.FailureEmptyReply,
            };
        }

        // The extractor's deadline is here and not on the whole turn, because the raise of the turn's
        // events and the stage advance must always run.
        private async Task<string?> ExtractAsync(ConversationTurn turn, AgentResponse response)
        {
            string? extractionFailure;

            using CancellationTokenSource deadline = new(TurnFailureReasons.CompletionTimeout, _session.Time);
            try
            {
                extractionFailure = await _session.Writers.ExtractAsync(turn, response, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                extractionFailure = TurnFailureReasons.ExtractionTimedOut;
            }

            if (extractionFailure is not null)
            {
                SessionFaults.Raise(_session, FaultKind.ExtractionFailed, turn.Index, extractionFailure, cause: null);
            }

            return extractionFailure;
        }

        private string AdvanceStage(ConversationTurn turn)
        {
            if (_session.Policy is null)
            {
                return turn.StageBefore;
            }

            string stageAfter = _session.Policy.Advance(_session.State.Snapshot());
            _session.State.Stage = stageAfter;
            // A host or a tool may have ended the conversation during this turn; a stage that is not terminal never
            // opens it again.
            _session.IsComplete = _session.Policy.IsTerminal || _session.Lifetime.Ending.Requested;

            return stageAfter;
        }

        /// <summary>Reads the closed outcome value of one finished turn.</summary>
        /// <param name="failure">The failure reason, or <see langword="null"/>.</param>
        /// <param name="interruptedAfter">The played duration, or <see langword="null"/>.</param>
        /// <returns>One of the three values the metric attribute takes.</returns>
        private static string Outcome(string? failure, TimeSpan? interruptedAfter)
        {
            return (failure, interruptedAfter) switch
            {
                (not null, _) => AgentCoreTelemetry.OutcomeFailed,
                (_, not null) => AgentCoreTelemetry.OutcomeInterrupted,
                _ => AgentCoreTelemetry.OutcomeCompleted,
            };
        }

        /// <summary>Reports calls the model named that no tool answered.</summary>
        /// <param name="turn">The turn that just ran.</param>
        /// <param name="response">What the agent answered.</param>
        private void ReportUndeclaredToolCalls(ConversationTurn turn, AgentResponse response)
        {
            HashSet<string> invoked = new(turn.Results.CallIds, StringComparer.Ordinal);
            HashSet<string> denied = new(turn.Results.DeniedCallIds.Concat(TurnApprovalAnswers.DeniedCallIds(turn.Spoken)), StringComparer.Ordinal);

            foreach (FunctionCallContent? call in response.Messages
                .SelectMany(message => message.Contents)
                .OfType<FunctionCallContent>()
                .Where(call => !invoked.Contains(call.CallId)))
            {
                // An approval denial answered the call: the tool is declared, and the refusal is the outcome.
                if (denied.Contains(call.CallId))
                {
                    if (_session.Hooks.Wants<ToolCalled>())
                    {
                        _ = _session.Hooks.Raise(new ToolCalled(
                            _session.Hooks.Scope(turn.Index, turn.StageBefore), call.Name, call.CallId, TimeSpan.Zero,
                            ToolOutcome.Blocked, Fatal: false, FailureKind: null, Failure: null));
                    }

                    continue;
                }

                _ = _session.Hooks.Raise(new ToolCalled(
                    _session.Hooks.Scope(turn.Index, turn.StageBefore), call.Name, call.CallId, TimeSpan.Zero,
                    ToolOutcome.Undeclared, Fatal: false, ToolFailureKind.Undeclared,
                    $"the model called '{call.Name}', and no such tool is declared."));
            }
        }
    }
}
