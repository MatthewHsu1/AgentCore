// Portions derived from LiveKit Agents, livekit-agents/livekit/agents/voice/agent_session.py:413
// (user_away_timeout default), commit d8405f132e1bd960f298190c18daf81ffc1faf45. Copyright 2023 LiveKit,
// Inc. Licensed under the Apache License, Version 2.0. Modified: translated to C#; LiveKit only emits
// user_state_changed and leaves "away" to the app. What to say is ours:
// a fixed prompt, not a hook.

using AgentCore.AspNetCore.Voice.Options;

namespace AgentCore.AspNetCore.Voice.Session
{
    /// <summary>When the caller counts as away, and what the agent says once they do.</summary>
    /// <param name="Timeout">
    /// How long both sides must stay <see cref="AgentState.Listening"/>/<see cref="UserState.Listening"/>
    /// before the caller is marked away. <see cref="VoiceOptions.Default"/> uses 15 s, LiveKit's default,
    /// with "Are you still there?"; a document that omits <c>userAway</c> keeps it. A
    /// <see langword="null"/> <see cref="VoiceOptions.UserAway"/>, from an explicit <c>userAway: null</c>,
    /// disables the timer. Marking the caller away never hangs up.
    /// </param>
    /// <param name="Say">What <see cref="VoiceSession.Say"/> speaks once the caller is marked away. Never empty.</param>
    internal sealed record UserAwayOptions(TimeSpan Timeout, string Say);
}
