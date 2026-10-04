// Portions derived from LiveKit Agents, livekit-agents/livekit/agents/voice/speech_handle.py,
// commit d8405f132e1bd960f298190c18daf81ffc1faf45. Copyright 2023 LiveKit, Inc.
// Licensed under the Apache License, Version 2.0. Modified: translated to C#.

namespace AgentCore.AspNetCore.Voice.Turns
{
    /// <summary>Why a speech was interrupted. The first source recorded against a handle wins.</summary>
    internal enum InterruptionSource
    {
        /// <summary>The caller started talking over the speech. Telnyx's <c>interrupt</c> frame maps here.</summary>
        AudioActivity,

        /// <summary>A committed user turn preempted the speech.</summary>
        UserTurn,

        /// <summary>Code did it: <c>VoiceSession.Interrupt()</c>, a tool, or teardown.</summary>
        Programmatic,
    }
}
