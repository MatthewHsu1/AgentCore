// Portions derived from LiveKit Agents, tests/test_filler.py (test_run_context_with_filler_yields_and_fires
// :380, test_run_context_with_filler_cancels_on_exit :399), commit
// d8405f132e1bd960f298190c18daf81ffc1faf45. Copyright 2023 LiveKit, Inc.
// Licensed under the Apache License, Version 2.0. Modified: translated to C#; a tool cannot open this
// itself (G5), so the scope is opened directly with the tool id's options rather than through
// RunContext.with_filler.

using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Voice;
using AgentCore.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Voice
{
    /// <summary>Opening and closing <see cref="ToolFillerScope"/> around a tool call's step.</summary>
    public sealed class ToolFillerScopeTests : IAsyncDisposable
    {
        private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;

        private readonly FakeTimeProvider _time = new(Start);

        private readonly FakeConversationOutput _output = new();

        private readonly VoiceSession _session;

        private readonly SpeechHandle _handle;

        public ToolFillerScopeTests()
        {
            _session = new VoiceSession(_output, _time, NullLogger.Instance);
            _handle = SpeechHandle.Create(_time, NullLogger.Instance);
        }

        public ValueTask DisposeAsync()
        {
            return _output.DisposeAsync();
        }

        // test_run_context_with_filler_yields_and_fires :380
        [Fact(Timeout = 30_000)]
        public async Task OpenForLongerThanTheDwell_FiresBeforeItCloses()
        {
            FillerOptions options = new("hello", TimeSpan.FromMilliseconds(20));

            await using (ToolFillerScope scope = new(_session, _handle, options))
            {
                await _time.WaitForTimersAsync(Start.AddMilliseconds(20), 1);
                _time.Advance(TimeSpan.FromMilliseconds(20));
                await _output.WaitForLogAsync(2);
            }

            Assert.Equal(["hello"], _output.Spoken);
        }

        // test_run_context_with_filler_cancels_on_exit :399
        [Fact(Timeout = 30_000)]
        public async Task ClosedBeforeTheDwellElapses_FiresNothingAndDisarmsTheDwell()
        {
            FillerOptions options = new("nope", TimeSpan.FromSeconds(1));

            await using (ToolFillerScope scope = new(_session, _handle, options))
            {
                await _time.WaitForTimersAsync(Start.AddSeconds(1), 1);
                _time.Advance(TimeSpan.FromMilliseconds(10));
            }

            Assert.True(_time.WaitForTimersAsync(Start.AddSeconds(1), 0).IsCompleted, "the dwell was still armed after the scope closed.");
            _time.Advance(TimeSpan.FromSeconds(1));
            Assert.Empty(_output.Spoken);
        }
    }
}
