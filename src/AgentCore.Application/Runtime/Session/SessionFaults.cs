using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Notices;

namespace AgentCore.Application.Runtime.Session
{
    /// <summary>Raises the non-fatal faults of one session.</summary>
    internal static class SessionFaults
    {
        internal static void Raise(ConversationSession session, FaultKind kind, int? turnIndex, string message, Exception? cause)
        {
            SessionHooks hooks = session.Hooks;
            _ = hooks.Raise(new Fault(hooks.Scope(turnIndex, stage: null), kind, message, cause));
        }
    }
}
