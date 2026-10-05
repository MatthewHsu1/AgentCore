using AgentCore.Application.Hooks.Notices;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Voice.Options;
using AgentCore.AspNetCore.Voice.Session;
using AgentCore.AspNetCore.Voice.Speech;
using AgentCore.AspNetCore.Voice.Turns;
using AgentCore.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Voice
{
    /// <summary>
    /// The four latency readings of a pipeline reply, taken at LiveKit's points off a clock the
    /// test owns.
    /// </summary>
    public sealed class TurnMetricsTests : IAsyncDisposable
    {
        private readonly FakeTimeProvider _time = new(DateTimeOffset.UnixEpoch);

        private readonly FakeConversationOutput _output = new();

        private readonly ScriptedVoicePort _port = new();

        private readonly LatencyRecorder _latency = new();

        private readonly VoiceActivity _activity;

        public TurnMetricsTests()
        {
            _activity = new VoiceActivity(new VoiceSession(_output, _time, NullLogger.Instance), _port, CancellationToken.None, latency: _latency.Record);
        }

        public ValueTask DisposeAsync()
        {
            return _output.DisposeAsync();
        }

        // A reply with no tool is one step, so each clock reports once, at LiveKit's points.
        [Fact(Timeout = 30_000)]
        public async Task AReplyThatEndsOnItsOwn_ReportsOneReadingOnEachOfTheThreeTurnClocks()
        {
            long userTurnEndedAt = _time.GetTimestamp();

            SpeechHandle handle = _activity.GenerateReply("one", userTurnEndedAt: userTurnEndedAt);
            ScriptedVoiceTurn turn = _port.Turn(1);
            _ = await turn.Started;

            _time.Advance(TimeSpan.FromMilliseconds(200));
            await turn.TextAsync("hello");
            await turn.TextAsync(" there");
            turn.End();
            _ = await handle;

            Assert.Equal([0.200], _latency.Of(LatencyMetric.TimeToFirstToken));
            Assert.Equal([0.200], _latency.Of(LatencyMetric.TimeToFirstSpeech));
            Assert.Equal([0.200], _latency.Of(LatencyMetric.TimeToReplyEnd));
        }

        [Fact(Timeout = 30_000)]
        public async Task ATwoStepReply_ReportsATimeToFirstTokenPerStepAndOneFirstSpeechAndReplyEnd()
        {
            long userTurnEndedAt = _time.GetTimestamp();
            _time.Advance(TimeSpan.FromMilliseconds(50));

            SpeechHandle handle = _activity.GenerateReply("What's the weather?", userTurnEndedAt: userTurnEndedAt);
            ScriptedVoiceTurn turn = _port.Turn(1);
            _ = await turn.Started;

            _time.Advance(TimeSpan.FromMilliseconds(100));
            await turn.TextAsync("Let me check.");
            await _output.WaitForLogAsync(1);
            await turn.CallAsync("1");
            await _output.WaitForLogAsync(2);

            _time.Advance(TimeSpan.FromMilliseconds(300));
            await turn.ResultAsync("1");
            _time.Advance(TimeSpan.FromMilliseconds(150));
            await turn.TextAsync("Sunny.");
            turn.End();
            _ = await handle;

            Assert.Equal([0.100, 0.150], _latency.Of(LatencyMetric.TimeToFirstToken));
            Assert.Equal([0.150], _latency.Of(LatencyMetric.TimeToFirstSpeech));
            Assert.Equal([0.600], _latency.Of(LatencyMetric.TimeToReplyEnd));
        }

        // EngineReplyStream.Start: no reading is taken once the speech is interrupted.
        [Fact(Timeout = 30_000)]
        public async Task ContentAfterTheSpeechWasInterrupted_TakesNoTimeToFirstTokenReading()
        {
            SpeechHandle handle = _activity.GenerateReply("Where is my order?", userTurnEndedAt: _time.GetTimestamp());
            ScriptedVoiceTurn turn = _port.Turn(1);
            _ = await turn.Started;

            _time.Advance(TimeSpan.FromMilliseconds(100));
            await turn.TextAsync("Let me look.");
            await turn.CallAsync("1");
            _port.EndTurnOnCut = false;
            _ = handle.Interrupt(source: InterruptionSource.UserTurn);

            await turn.ResultAsync("1");
            await turn.TextAsync("It shipped.");
            turn.End();
            _ = await handle;

            Assert.Equal([0.100], _latency.Of(LatencyMetric.TimeToFirstToken));
        }

        [Fact(Timeout = 30_000)]
        public async Task ABargeIn_ReportsTheTimeUntilTheSpeechIsDoneAndNoReplyEnd()
        {
            long userTurnEndedAt = _time.GetTimestamp();

            SpeechHandle handle = _activity.GenerateReply("Tell me a story.", userTurnEndedAt: userTurnEndedAt);
            ScriptedVoiceTurn turn = _port.Turn(1);
            _ = await turn.Started;

            _time.Advance(TimeSpan.FromMilliseconds(80));
            await turn.TextAsync("Once upon a time");
            await _output.WaitForLogAsync(1);

            // The engine ends its turn only when the test says, so the speech stays not done meanwhile.
            _port.EndTurnOnCut = false;
            Task interrupted = _activity.InterruptByAudioActivity("Once", TimeSpan.FromMilliseconds(400));
            await Poll.UntilAsync(() => _port.Cuts.Count == 1);
            _time.Advance(TimeSpan.FromMilliseconds(120));
            turn.End();
            await interrupted;

            Assert.True(handle.IsDone);
            Assert.Equal([0.120], _latency.Of(LatencyMetric.BargeIn));
            Assert.Equal([0.080], _latency.Of(LatencyMetric.TimeToFirstToken));
            Assert.Equal([0.080], _latency.Of(LatencyMetric.TimeToFirstSpeech));
            Assert.Empty(_latency.Of(LatencyMetric.TimeToReplyEnd));
        }

        // agent_activity.py interrupt: the barge-in reading is taken once the interrupted speech is done, also when a
        // final prompt interrupted it first and it waits for this report; a repeated report takes no second reading.
        [Fact(Timeout = 30_000)]
        public async Task ABargeInAfterAFinalPrompt_ReportsOneReadingOnceTheSpeechIsDone()
        {
            SpeechHandle handle = _activity.GenerateReply("hi", userTurnEndedAt: _time.GetTimestamp());
            ScriptedVoiceTurn turn = _port.Turn(1);
            _ = await turn.Started;
            await turn.TextAsync("Hello there, ");
            await _output.WaitForLogAsync(1);

            _port.EndTurnOnCut = false;
            _activity.InterruptByFinalTranscript();
            await _time.WaitForTimersAsync(_time.GetUtcNow() + VoiceOptions.DefaultHeardTextWait, 1);
            Task recorded = _activity.InterruptByAudioActivity("Hello", TimeSpan.FromMilliseconds(400));
            Task repeated = _activity.InterruptByAudioActivity("Hello", TimeSpan.FromMilliseconds(400));
            _time.Advance(TimeSpan.FromMilliseconds(150));
            turn.End();
            await Task.WhenAll(recorded, repeated);
            _ = await handle;

            Assert.Equal([0.150], _latency.Of(LatencyMetric.BargeIn));
        }

        // generation.py perform_llm_inference: ttft is measured from a step's start, so with no step there is none.
        [Fact]
        public void FirstContentBeforeAnyStep_ReportsNoTimeToFirstToken()
        {
            TurnMetrics metrics = new(_time, _time.GetTimestamp(), _latency.Record);

            metrics.MarkFirstContent();

            Assert.Empty(_latency.Of(LatencyMetric.TimeToFirstToken));
        }

        [Fact]
        public void EndingAReplyTwice_ReportsOneReplyEnd()
        {
            long userTurnEndedAt = _time.GetTimestamp();
            TurnMetrics metrics = new(_time, userTurnEndedAt, _latency.Record);
            _time.Advance(TimeSpan.FromMilliseconds(300));
            metrics.MarkSpeechEnded();

            metrics.EndReply();
            _time.Advance(TimeSpan.FromMilliseconds(100));
            metrics.MarkSpeechEnded();
            metrics.EndReply();

            Assert.Equal([0.300], _latency.Of(LatencyMetric.TimeToReplyEnd));
        }
    }
}
