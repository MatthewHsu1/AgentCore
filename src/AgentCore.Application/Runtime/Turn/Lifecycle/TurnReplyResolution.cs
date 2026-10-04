using System.Text.Json;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Domain;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentCore.Application.Runtime.Cut;
using AgentCore.Application.Runtime.Session;
using AgentCore.Application.Runtime.ToolCalls;

namespace AgentCore.Application.Runtime.Turn.Lifecycle
{
    /// <summary>Decides what one turn said, and why, from how its run ended.</summary>
    internal static class TurnReplyResolution
    {
        /// <summary>Reads the reply, the failure, and the approvals still pending of one finished turn.</summary>
        /// <param name="session">The conversation the turn belongs to.</param>
        /// <param name="turn">The turn that just ran.</param>
        /// <param name="response">What the agent answered.</param>
        /// <param name="interruption">The cut the turn was sealed with, or <see langword="null"/>.</param>
        /// <param name="disposition">What the pipeline layers reported, or <see langword="null"/>.</param>
        /// <param name="fault">The fault that threw out of the run, or <see langword="null"/>.</param>
        /// <param name="callerFacing">Whether the turn keeps only the one line the user was shown, as a graph row does.</param>
        /// <returns>What the turn said, what the caller heard, and why they differ if they do.</returns>
        internal static ReplyOutcome Resolve(
            ConversationSession session,
            ConversationTurn turn,
            AgentResponse response,
            TurnCut? interruption,
            TurnDisposition? disposition,
            Exception? fault,
            bool callerFacing)
        {
            if (fault is null && disposition is { Fallback: FallbackCause.Faulted, FallbackFault: { } caught })
            {
                fault = caught;
            }

            bool isToolFault = fault is not null && ToolFaultMark.IsOn(fault);

            string reply = ReplyText.From(response.Messages);

            string generatedText = callerFacing ? reply : TurnMessages.AllText(response.Messages);

            TimeSpan? interruptedAfter = null;

            string? toolFault = isToolFault ? fault!.Message : null;

            string? failure = fault is null
                ? null
                : (isToolFault ? TurnFailureReasons.ToolFailure : TurnFailureReasons.RunFault)
                    + " " + fault.Message;

            List<PendingApproval> approvals = ApprovalRequestsOf(response);
            if (approvals.Count > 0 && session.Hooks.Wants<ApprovalChanged>())
            {
                RaiseAsked(session, turn, response);
            }

            if (interruption is { } cut)
            {
                reply = (cut.ShownText ?? generatedText).Trim();
                interruptedAfter = cut.Played ?? TimeSpan.Zero;
            }
            else if (approvals.Count == 0 && (failure is not null
                || string.IsNullOrWhiteSpace(reply)
                || disposition?.Fallback is FallbackCause.EmptyReply))
            {
                failure ??= TurnFailureReasons.EmptyReply;
                reply = session.Compiled.FallbackReply;
            }

            return new ReplyOutcome(reply, generatedText, failure, toolFault, interruptedAfter, approvals, fault, isToolFault);
        }

        /// <summary>Raises one <see cref="ApprovalChanged"/> per request the turn leaves for a human, under the function call id.</summary>
        private static void RaiseAsked(ConversationSession session, ConversationTurn turn, AgentResponse response)
        {
            HashSet<string> reshown = new(turn.Results.ReshownCallIds, StringComparer.Ordinal);
            foreach (ToolApprovalRequestContent request in response.Messages
                .SelectMany(message => message.Contents)
                .OfType<ToolApprovalRequestContent>())
            {
                if (request.ToolCall is FunctionCallContent call && !reshown.Contains(call.CallId))
                {
                    _ = session.Hooks.Raise(new ApprovalChanged(
                        session.Hooks.Scope(turn.Index, turn.StageBefore), call.Name, call.CallId, ApprovalState.Asked, ApprovalBy.Human, Reason: null));
                }
            }
        }

        /// <summary>Reads the approval requests one finished turn still waits on.</summary>
        /// <param name="response">What the agent answered.</param>
        /// <returns>One entry per request content, oldest first; empty when the turn asked nothing.</returns>
        private static List<PendingApproval> ApprovalRequestsOf(AgentResponse response)
        {
            List<PendingApproval> approvals = [];
            foreach (ToolApprovalRequestContent request in response.Messages
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
    }
}
