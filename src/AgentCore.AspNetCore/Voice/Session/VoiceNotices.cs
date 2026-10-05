using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.AspNetCore.Voice.Session
{
    /// <summary>Raises <see cref="VoiceStateChanged"/> for every voice state change of a session, on the hooks of the conversation it speaks for.</summary>
    internal static class VoiceNotices
    {
        /// <summary>The hooks of the conversation behind a port, or <see langword="null"/> for a port that is not a session.</summary>
        internal static SessionHooks? HooksOf(IConversationPort port) => (port as ConversationSession)?.Hooks;

        /// <summary>Subscribes for the life of the session. <paramref name="hooks"/> is read per change, so a rebound port is followed.</summary>
        internal static void Attach(VoiceSession session, Func<SessionHooks?> hooks)
        {
            session.AgentStateChanged += change => Raise(hooks(), VoiceParty.Agent, Of(change.OldState), Of(change.NewState));
            session.UserStateChanged += change => Raise(hooks(), VoiceParty.User, Of(change.OldState), Of(change.NewState));
        }

        private static void Raise(SessionHooks? hooks, VoiceParty party, VoiceState old, VoiceState now)
        {
            if (hooks is not null && old != now && hooks.Wants<VoiceStateChanged>())
            {
                _ = hooks.Raise(new VoiceStateChanged(hooks.Scope(turnIndex: null, stage: null), party, old, now));
            }
        }

        private static VoiceState Of(AgentState state) => state switch
        {
            AgentState.Thinking => VoiceState.Thinking,
            AgentState.Speaking => VoiceState.Speaking,
            AgentState.Initializing or AgentState.Idle or AgentState.Listening => VoiceState.Listening,
            _ => VoiceState.Listening,
        };

        private static VoiceState Of(UserState state) => state switch
        {
            UserState.Speaking => VoiceState.Speaking,
            UserState.Away => VoiceState.Away,
            UserState.Listening => VoiceState.Listening,
            _ => VoiceState.Listening,
        };
    }
}
