using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Runtime.Turn.Lifecycle
{
    /// <summary>
    /// Raises the refusal of a turn that keeps none of its words, so it leaves a notice even when the client
    /// that could read the error is gone.
    /// </summary>
    internal static class TurnRefusals
    {
        /// <summary>Raises the notice of a refusal: busy, conflict, gone, dropped, disposed, terminal, in use.</summary>
        /// <param name="session">The conversation.</param>
        /// <param name="turnIndex">
        /// The turn the session ran, or would have run, or <see langword="null"/> for a turn refused before it read
        /// the conversation: which index it would have taken is not known then.
        /// </param>
        /// <param name="reason">Why.</param>
        internal static void Raise(ConversationSession session, int? turnIndex, TurnRefusal reason)
        {
            SessionHooks hooks = session.Hooks;
            bool afterEnd = session.Lifetime.Ending.Requested || reason == TurnRefusal.Terminal;
            lock (session.TurnLock)
            {
                if (!afterEnd)
                {
                    hooks.EnsureStarted();
                }

                _ = hooks.Raise(new TurnRefused(hooks.Scope(turnIndex, stage: null), reason, afterEnd));
            }
        }
    }
}
