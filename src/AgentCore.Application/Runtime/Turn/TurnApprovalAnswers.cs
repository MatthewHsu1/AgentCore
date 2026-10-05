using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Transcript;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime.Turn
{
    /// <summary>The approval answers a person sent as one turn's message, whichever door they came in by.</summary>
    internal static class TurnApprovalAnswers
    {
        /// <summary>What a request is refused with when the caller sends new words instead of answering it.</summary>
        internal const string MovedOnReason = "the user moved on without answering.";

        /// <summary>
        /// Puts a refusal of every open request in front of the caller's new words, so the turn answers what the
        /// conversation still asks and then runs the words. The refusals ride the turn's own user message, so the
        /// record keeps one answer per request.
        /// </summary>
        /// <param name="words">What the caller sent.</param>
        /// <param name="open">The requests no answer covers yet.</param>
        /// <returns>A copy of <paramref name="words"/> led by one refusal per open request.</returns>
        internal static ChatMessage MovedOn(ChatMessage words, IEnumerable<ToolApprovalRequestContent> open)
        {
            ChatMessage refused = words.Clone();
            refused.Contents = [.. open.Select(static request => (AIContent)request.CreateResponse(false, MovedOnReason)), .. words.Contents];
            return refused;
        }

        /// <summary>
        /// Reads the approval requests an edit withdraws with the turns that asked them, so no answer is written for
        /// them. Only a parent the session holds is read: an edit under the summary keeps the requests it cannot see.
        /// </summary>
        /// <param name="history">The message store.</param>
        /// <param name="session">The session the conversation's words live on.</param>
        /// <param name="origin">Where the turn hangs, or <see langword="null"/> for a turn that is no edit.</param>
        /// <returns>The ids of the requests; empty when the turn withdraws none.</returns>
        internal static IReadOnlySet<string> Withdrawn(AgentCoreChatHistoryProvider history, AgentSession session, ConversationTurnOrigin? origin)
        {
            return origin is { NamesParent: true } edit && history.RowsAfter(session, edit.ParentMessageId) is { Held: true } after
                ? new HashSet<string>(
                    after.Rows.SelectMany(static row => row.Contents).OfType<ToolApprovalRequestContent>().Select(static request => request.RequestId),
                    StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
        }

        /// <summary>Raises one <see cref="ApprovalChanged"/> per answer the turn takes in, and settles each call's open BeforeToolApproval decision.</summary>
        /// <param name="hooks">The session's hooks.</param>
        /// <param name="turn">The turn that just began.</param>
        internal static void TakeIn(SessionHooks hooks, ConversationTurn turn)
        {
            foreach ((ToolApprovalResponseContent answer, FunctionCallContent call) in Of(turn.Spoken))
            {
                _ = hooks.ApprovalDecisions.TryRemove(call.CallId, out _);
                if (hooks.Wants<ApprovalChanged>())
                {
                    _ = hooks.Raise(new ApprovalChanged(
                        hooks.Scope(turn.Index, turn.StageBefore), call.Name, call.CallId,
                        answer.Approved ? ApprovalState.Approved : ApprovalState.Denied, ApprovalBy.Human, Reason: answer.Reason));
                }
            }
        }

        /// <summary>The ids of the calls a person refused in the turn's message.</summary>
        internal static IEnumerable<string> DeniedCallIds(ChatMessage spoken)
        {
            return Of(spoken).Where(static pair => !pair.Answer.Approved).Select(static pair => pair.Call.CallId);
        }

        private static IEnumerable<(ToolApprovalResponseContent Answer, FunctionCallContent Call)> Of(ChatMessage spoken)
        {
            foreach (ToolApprovalResponseContent answer in spoken.Contents.OfType<ToolApprovalResponseContent>())
            {
                if (answer.ToolCall is FunctionCallContent call)
                {
                    yield return (answer, call);
                }
            }
        }
    }
}
