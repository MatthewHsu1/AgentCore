// Portions derived from LiveKit Agents, livekit-agents/livekit/agents/voice/agent_activity.py:2097-2098,
// 2132-2133 (the closed/closing check inside wait_for_idle, raising ActivityClosedError),
// commit d8405f132e1bd960f298190c18daf81ffc1faf45. Copyright 2023 LiveKit, Inc.
// Licensed under the Apache License, Version 2.0. Modified: translated to C#; one VoiceSession per
// conversation, so the flag belongs to the session rather than to a separate activity object.

namespace AgentCore.AspNetCore.Voice.Session
{
    /// <summary>The session closed, or was already closing, while something waited on it.</summary>
    internal sealed class VoiceSessionClosedException() : Exception("the voice session is closing.");
}
