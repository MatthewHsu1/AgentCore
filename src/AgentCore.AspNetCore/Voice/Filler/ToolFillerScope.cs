// Portions derived from LiveKit Agents, livekit-agents/livekit/agents/voice/events.py:112-153
// (RunContext.with_filler), commit d8405f132e1bd960f298190c18daf81ffc1faf45. Copyright 2023 LiveKit,
// Inc. Licensed under the Apache License, Version 2.0. Modified: translated to C#; a tool cannot open
// this itself, so it is a scope PipelineReply opens from voice options on a FunctionCallContent and
// closes on the matching FunctionResultContent or the speech's interruption, rather than an
// async context manager a tool's own code enters.

using AgentCore.AspNetCore.Voice.Session;
using AgentCore.AspNetCore.Voice.Speech;

namespace AgentCore.AspNetCore.Voice.Filler
{
    /// <summary>The filler scheduled for one tool call, open for as long as its step is still running.</summary>
    /// <param name="session">Where idleness, state changes, and <c>say</c> come from.</param>
    /// <param name="speechHandle">The speech this tool call's step belongs to.</param>
    /// <param name="options">This tool id's filler, from voice options.</param>
    internal sealed class ToolFillerScope(VoiceSession session, SpeechHandle speechHandle, FillerOptions options) : IAsyncDisposable
    {
        private readonly FillerScheduler _scheduler = new(session, speechHandle, options.Source, options.Delay, options.Interval, options.MaxSteps);

        /// <summary>Gets every speech this scope's filler created, in fire order.</summary>
        public IReadOnlyList<SpeechHandle> CreatedSpeeches => _scheduler.CreatedSpeeches;

        public ValueTask DisposeAsync()
        {
            return new ValueTask(_scheduler.CloseAsync());
        }
    }
}
