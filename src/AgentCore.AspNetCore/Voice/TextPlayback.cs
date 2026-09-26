// Portions derived from LiveKit Agents, livekit-agents/livekit/agents/voice/generation.py:679-693
// (_ForwardOutput.played, text-only branches), commit d8405f132e1bd960f298190c18daf81ffc1faf45.
// Copyright 2023 LiveKit, Inc. Licensed under the Apache License, Version 2.0.
// Modified: translated to C#; the "full"/"partial"/"skipped" values LiveKit's audio playback can also
// reach are dropped, since nothing here plays audio.

namespace AgentCore.AspNetCore.Voice
{
    /// <summary>Whether a forwarded piece of text reached the caller, in full, in part, or not at all.</summary>
    internal enum TextPlayback
    {
        /// <summary>Nothing was ever forwarded.</summary>
        Skipped,

        /// <summary>Some text reached the output before the speech was interrupted.</summary>
        Partial,

        /// <summary>Every fragment reached the output and the speech was not interrupted.</summary>
        Full,
    }
}
