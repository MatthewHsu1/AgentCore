// Portions derived from LiveKit Agents, livekit-agents/livekit/agents/voice/events.py:309-324,
// commit d8405f132e1bd960f298190c18daf81ffc1faf45. Copyright 2023 LiveKit, Inc.
// Licensed under the Apache License, Version 2.0. Modified: translated to C#.

namespace AgentCore.AspNetCore.Voice.Session
{
    /// <summary>What the caller is doing right now.</summary>
    internal enum UserState
    {
        /// <summary>An interim or final prompt is arriving.</summary>
        Speaking,

        /// <summary>Not talking.</summary>
        Listening,

        /// <summary>Silent long enough that <c>userAway</c> fires.</summary>
        Away,
    }
}
