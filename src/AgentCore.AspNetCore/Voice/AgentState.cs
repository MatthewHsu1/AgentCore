// Portions derived from LiveKit Agents, livekit-agents/livekit/agents/voice/events.py:309-324,
// commit d8405f132e1bd960f298190c18daf81ffc1faf45. Copyright 2023 LiveKit, Inc.
// Licensed under the Apache License, Version 2.0. Modified: translated to C#.

namespace AgentCore.AspNetCore.Voice
{
    /// <summary>What the agent is doing right now.</summary>
    internal enum AgentState
    {
        /// <summary>The session has not started yet.</summary>
        Initializing,

        /// <summary>Started, nothing running: no reply, no tool call, no queued speech.</summary>
        Idle,

        /// <summary>Ready for the caller, nothing queued to say.</summary>
        Listening,

        /// <summary>A model step or a tool call is running; nothing has reached the output yet.</summary>
        Thinking,

        /// <summary>Forwarding words to the output port.</summary>
        Speaking,
    }
}
