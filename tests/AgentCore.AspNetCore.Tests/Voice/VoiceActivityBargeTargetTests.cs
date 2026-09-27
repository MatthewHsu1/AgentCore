// Portions derived from LiveKit Agents, livekit-agents/livekit/agents/voice/agent_activity.py:2310-2375
// (_interrupt_by_audio_activity), commit d8405f132e1bd960f298190c18daf81ffc1faf45. Copyright 2023 LiveKit, Inc.
// Licensed under the Apache License, Version 2.0. Modified: translated to C#; driven over a scripted engine turn.

using AgentCore.Application.Runtime;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Voice;
using AgentCore.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Voice
{
    /// <summary>Which speech <see cref="VoiceActivity.InterruptByAudioActivity"/> cuts: only one still running, not
    /// yet interrupted, that allows interruptions, as LiveKit's <c>_interrupt_by_audio_activity</c> checks.</summary>
    public sealed class VoiceActivityBargeTargetTests : IAsyncDisposable
    {
        private readonly FakeTimeProvider _time = new(DateTimeOffset.UnixEpoch);

        private readonly FakeConversationOutput _output = new();

        private readonly ScriptedVoicePort _port = new();

        private readonly VoiceSession _session;

        public VoiceActivityBargeTargetTests()
        {
            _session = new VoiceSession(_output, _time, NullLogger.Instance);
        }

        public ValueTask DisposeAsync()
        {
            return _output.DisposeAsync();
        }

        [Fact(Timeout = 30_000)]
        public async Task ABargeInOnASpeakingReplyThatDisallowsInterruptions_CutsNothing()
        {
            VoiceActivity activity = new(_session, _port, CancellationToken.None, allowInterruptions: false);
            SpeechHandle reply = activity.GenerateReply("Read me the terms.");
            ScriptedVoiceTurn turn = _port.Turn(1);
            await turn.TextAsync("The terms are");
            await _output.WaitForLogAsync(1);

            await activity.InterruptByAudioActivity("The terms", TimeSpan.FromSeconds(1));

            Assert.Empty(_port.Cuts);
            Assert.False(reply.IsInterrupted);

            await turn.TextAsync(" as follows.");
            turn.End();
            _ = await reply;
            Assert.Equal(["The terms are", " as follows."], _output.Spoken);
        }

        [Fact(Timeout = 30_000)]
        public async Task ABargeInWhileAReplyThatDisallowsInterruptionsWaitsOnATool_CutsNothing()
        {
            VoiceActivity activity = new(_session, _port, CancellationToken.None, allowInterruptions: false);
            SpeechHandle reply = activity.GenerateReply("Where is my order?");
            ScriptedVoiceTurn turn = _port.Turn(1);
            await turn.TextAsync("Let me look that up.");
            await turn.CallAsync("1", "lookup_order");
            await Poll.UntilAsync(() => _session.HasBackgroundSpeeches && _session.Scheduler.CurrentSpeech is null);

            await activity.InterruptByAudioActivity("Let me look", TimeSpan.FromMilliseconds(900));

            Assert.Empty(_port.Cuts);
            Assert.False(reply.IsInterrupted);

            await turn.ResultAsync("1");
            await turn.TextAsync("It shipped.");
            turn.End();
            _ = await reply;
            Assert.Equal(["Let me look that up.", "It shipped."], _output.Spoken);
        }

        // Handoff 2026-09-22, step 7 open issues: the first cut wins, so a transport report that arrives once a
        // user turn has already interrupted the speech is refused.
        [Fact(Timeout = 30_000)]
        public async Task ABargeInOnAReplyAlreadyInterrupted_LeavesTheCutToThatInterruption()
        {
            TaskCompletionSource held = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _output.BeforeSpeakReturns = (_, _) => held.Task;
            VoiceActivity activity = new(_session, _port, CancellationToken.None);
            SpeechHandle reply = activity.GenerateReply("Tell me a story.");
            await _port.Turn(1).TextAsync("Here is a long story");
            await _output.WaitForLogAsync(1);
            _ = reply.Interrupt(source: InterruptionSource.UserTurn);

            Task barge = activity.InterruptByAudioActivity("Here is", TimeSpan.FromSeconds(1));

            Assert.Empty(_port.Cuts);

            _ = held.TrySetResult();
            await barge;
            _ = await reply;
            Assert.Equal([(0, new TurnCut("Here is a long story", null))], _port.Cuts);
            Assert.Equal(InterruptionSource.UserTurn, reply.InterruptSource);
        }

        // VoiceActivity.InterruptByAudioActivity: the engine holds the cut before the transport's next frame is read,
        // not once the speech's own teardown runs.
        [Fact(Timeout = 30_000)]
        public async Task ABargeIn_CutsTheTurnBeforeItReturns()
        {
            TaskCompletionSource held = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _output.BeforeSpeakReturns = (_, _) => held.Task;
            VoiceActivity activity = new(_session, _port, CancellationToken.None);
            SpeechHandle reply = activity.GenerateReply("Tell me a story.");
            await _port.Turn(1).TextAsync("Here is a long story");
            await _output.WaitForLogAsync(1);

            Task recorded = activity.InterruptByAudioActivity("Here is a", TimeSpan.FromSeconds(2));

            Assert.Equal([(0, new TurnCut("Here is a", TimeSpan.FromSeconds(2)))], _port.Cuts);

            _ = held.TrySetResult();
            await recorded;
            _ = await reply;
        }
    }
}
