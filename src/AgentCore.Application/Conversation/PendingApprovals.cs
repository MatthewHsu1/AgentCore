using AgentCore.Application.Runtime.Harness;
using AgentCore.Domain;

namespace AgentCore.Application.Conversation
{
    /// <summary>
    /// The tool calls a stored conversation still waits on a human answer for, for host history
    /// routes to re-arm approvals after a reload. The source is the provider state the asking turn
    /// filed: the pending queue minus the answers held or already collected.
    /// </summary>
    public static class PendingApprovals
    {
        /// <summary>Reads what a stored conversation still waits on.</summary>
        /// <param name="stored">The conversation, as the store holds it.</param>
        /// <returns>
        /// One entry per open request, oldest first, shaped like <see cref="TurnResult.Approvals"/>;
        /// empty when nothing waits on the caller.
        /// </returns>
        public static IReadOnlyList<PendingApproval> Read(StoredConversation stored)
        {
            ArgumentNullException.ThrowIfNull(stored);

            return Read(stored.Conversation);
        }

        /// <summary>Reads what a conversation row still waits on.</summary>
        /// <param name="record">The conversation's row, carrying the state its last turn filed.</param>
        /// <returns>
        /// One entry per open request, oldest first, shaped like <see cref="TurnResult.Approvals"/>;
        /// empty when nothing waits on the caller.
        /// </returns>
        public static IReadOnlyList<PendingApproval> Read(ConversationRecord record)
        {
            ArgumentNullException.ThrowIfNull(record);

            return record.State?.Providers is { } providers ? PendingApprovalQueue.OpenOf(providers) : [];
        }
    }
}
