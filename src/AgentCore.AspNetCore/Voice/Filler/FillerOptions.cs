// Portions derived from LiveKit Agents, livekit-agents/livekit/agents/voice/events.py:112-131
// (RunContext.with_filler's parameters), commit d8405f132e1bd960f298190c18daf81ffc1faf45.
// Copyright 2023 LiveKit, Inc. Licensed under the Apache License, Version 2.0. Modified: translated to
// C#; a tool cannot open this itself, so it is voice-options input to ToolFillerScope instead of a
// RunContext call. Per tool id, off by default.

namespace AgentCore.AspNetCore.Voice.Filler
{
    /// <summary>One tool id's filler: what to say while its step is still running, and how often.</summary>
    /// <param name="Source">What to say. See <see cref="FillerSource"/>.</param>
    /// <param name="Delay">How long the session must stay continuously idle before the first fire.</param>
    /// <param name="Interval">
    /// The wall-clock cooldown after a fire before the dwell restarts. <see langword="null"/> fires at most once.
    /// </param>
    /// <param name="MaxSteps">The most fires across the tool call's lifetime. <see langword="null"/> means no limit.</param>
    internal sealed record FillerOptions(FillerSource Source, TimeSpan Delay, TimeSpan? Interval = null, int? MaxSteps = null);
}
