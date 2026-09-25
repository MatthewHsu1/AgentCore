using AgentCore.Domain.Audit;

namespace AgentCore.Application.Runtime
{
    /// <summary>
    /// Raises <c>turn.refused</c> for a turn that keeps none of its words, so the refusal leaves a log line and an audit
    /// row even when the client that could read the error is gone.
    /// </summary>
    internal static class TurnRefusals
    {
        /// <summary>Another turn held the conversation past <see cref="ConversationBusyMark.WaitLimit"/>.</summary>
        internal const string Busy = "busy";

        /// <summary>The store refused the turn's words: another session saved that turn first.</summary>
        internal const string Conflict = "conflict";

        /// <summary>The client left while the turn waited for the conversation.</summary>
        internal const string Gone = "gone";

        /// <summary>This session was still running a turn.</summary>
        internal const string Running = "running";

        /// <summary>The caller disposed the turn's run before it read the reply, so the model never ran.</summary>
        internal const string Dropped = "dropped";

        /// <summary>Raises the refusal, unless the conversation's chain already closed.</summary>
        /// <param name="session">The conversation.</param>
        /// <param name="turnIndex">
        /// The turn the session ran, or would have run, or <see langword="null"/> for a turn refused before it read
        /// the conversation: which index it would have taken is not known then.
        /// </param>
        /// <param name="reason">One of the tokens above.</param>
        internal static void Raise(ConversationSession session, int? turnIndex, string reason)
        {
            if (session.Events.HasEnded)
            {
                return;
            }

            _ = session.Events.Raise(
                ConversationEventKind.TurnRefused,
                session.Time.GetUtcNow(),
                turnIndex,
                payload: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AuditPayloadKeys.RefusedReason] = reason,
                });
        }
    }
}
