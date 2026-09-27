// Portions derived from LiveKit Agents, livekit-agents/livekit/agents/voice/events.py:112-153
// (RunContext.with_filler), commit d8405f132e1bd960f298190c18daf81ffc1faf45. Copyright 2023 LiveKit,
// Inc. Licensed under the Apache License, Version 2.0. Modified: translated to C#; a tool cannot open
// this itself (G5), so it is a scope PipelineReply opens from voice options on a FunctionCallContent and
// closes on the matching FunctionResultContent or the speech's interruption (plan 2.4), rather than an
// async context manager a tool's own code enters.

namespace AgentCore.AspNetCore.Voice
{
    /// <summary>The filler scheduled for one tool call, open for as long as its step is still running.</summary>
    internal sealed class ToolFillerScope : IAsyncDisposable
    {
        private readonly FillerScheduler _scheduler;

        /// <param name="session">Where idleness, state changes, and <c>say</c> come from.</param>
        /// <param name="speechHandle">The speech this tool call's step belongs to.</param>
        /// <param name="options">This tool id's filler, from voice options (plan Q1/owner ruling).</param>
        public ToolFillerScope(VoiceSession session, SpeechHandle speechHandle, FillerOptions options)
        {
            _scheduler = new FillerScheduler(session, speechHandle, options.Source, options.Delay, options.Interval, options.MaxSteps);
        }

        /// <summary>Gets every speech this scope's filler created, in fire order.</summary>
        public IReadOnlyList<SpeechHandle> CreatedSpeeches => _scheduler.CreatedSpeeches;

        public ValueTask DisposeAsync()
        {
            return new ValueTask(_scheduler.CloseAsync());
        }
    }
}
