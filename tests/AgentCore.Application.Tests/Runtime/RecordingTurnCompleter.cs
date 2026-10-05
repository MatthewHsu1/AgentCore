using AgentCore.Application.Conversation;
using AgentCore.Application.Transcript;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentCore.Application.Runtime.Cut;
using AgentCore.Application.Runtime.Turn.Lifecycle;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// Stands in for the conversation's completer: its state is
    /// <c>stage-after:</c> plus the reply, so a test can tell the state after the stage advance from any other.
    /// It logs <c>staged</c> when the framework's hook had staged the run by the time it ran, then <c>complete</c>.
    /// </summary>
    internal sealed class RecordingTurnCompleter(
        AgentCoreChatHistoryProvider history, AgentSession historySession, List<string> log) : ITurnCompleter
    {
        /// <summary>Gets what the hook had staged when the completer ran.</summary>
        public IReadOnlyList<ChatMessage> StagedAtComplete { get; private set; } = [];

        /// <summary>Gets the ids the commit reported.</summary>
        public (string UserMessageId, string? ReplyMessageId)? Ids { get; private set; }

        /// <summary>Gets the turns an edit withdrew, or <see langword="null"/>.</summary>
        public WithdrawnTurns? Withdrawn { get; private set; }

        public void Superseded(WithdrawnTurns withdrawn)
        {
            Withdrawn = withdrawn;
        }

        public ValueTask<TurnCommit> CompleteAsync(TurnCommit sealing, Exception? fault)
        {
            StagedAtComplete = history.Staged(historySession);
            if (StagedAtComplete.Count > 0)
            {
                log.Add("staged");
            }

            log.Add("complete");

            string reply = fault is not null ? "fallback"
                : (sealing.Cut?.ShownText ?? ReplyText.From(sealing.Seen?.Messages ?? [])).Trim();
            return ValueTask.FromResult(sealing with
            {
                Completed = sealing.Cut is null && sealing.Disposition is null && fault is null,
                Reply = reply,
                State = new ConversationSessionState { Stage = "stage-after:" + reply },
            });
        }

        public async ValueTask<bool> RefusedAsync(Task<bool> refused)
        {
            return await refused;
        }

        public void Committed((string UserMessageId, string? ReplyMessageId)? ids, string spoken, TurnCut? late, TurnCut? recut)
        {
            Ids = ids;
        }

        public ValueTask FinishAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
