// Portions derived from LiveKit Agents, livekit-agents/livekit/agents/voice/events.py:309-324
// (UserStateChangedEvent), commit d8405f132e1bd960f298190c18daf81ffc1faf45. Copyright 2023 LiveKit, Inc.
// Licensed under the Apache License, Version 2.0. Modified: translated to C#; raised as a plain event,
// not a session-wide pub/sub topic.

namespace AgentCore.AspNetCore.Voice
{
    /// <summary>Raised each time <see cref="VoiceSession"/>'s user state actually changes.</summary>
    /// <param name="OldState">The state before the change.</param>
    /// <param name="NewState">The state after the change.</param>
    /// <param name="CreatedAt">When the change happened.</param>
    internal sealed record UserStateChanged(UserState OldState, UserState NewState, DateTimeOffset CreatedAt);
}
