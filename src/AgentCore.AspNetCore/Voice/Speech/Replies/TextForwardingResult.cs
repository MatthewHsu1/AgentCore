// Portions derived from LiveKit Agents, livekit-agents/livekit/agents/voice/generation.py:678-695
// (_ForwardOutput, text-only fields), commit d8405f132e1bd960f298190c18daf81ffc1faf45.
// Copyright 2023 LiveKit, Inc. Licensed under the Apache License, Version 2.0.
// Modified: translated to C#; every audio field dropped.

namespace AgentCore.AspNetCore.Voice.Speech.Replies
{
    /// <summary>What <see cref="TextForwarding.ForwardAsync"/> forwarded.</summary>
    /// <param name="Text">Every fragment the source yielded, concatenated in order.</param>
    /// <param name="Playback">Whether it reached the caller in full, in part, or not at all.</param>
    internal readonly record struct TextForwardingResult(string Text, TextPlayback Playback);
}
