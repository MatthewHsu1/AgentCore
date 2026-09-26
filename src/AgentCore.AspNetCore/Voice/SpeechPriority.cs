// Portions derived from LiveKit Agents, livekit-agents/livekit/agents/voice/speech_handle.py,
// commit d8405f132e1bd960f298190c18daf81ffc1faf45. Copyright 2023 LiveKit, Inc.
// Licensed under the Apache License, Version 2.0. Modified: translated to C#.

namespace AgentCore.AspNetCore.Voice
{
    /// <summary>How a speech is ordered against others waiting in the speech queue.</summary>
    internal enum SpeechPriority
    {
        /// <summary>Played after every other message already in the queue.</summary>
        Low = 0,

        /// <summary>The default priority for every speech the agent generates.</summary>
        Normal = 5,

        /// <summary>An important message, played before others.</summary>
        High = 10,
    }
}
