using AgentCore.Application.Runtime.Cut;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Voice.Options;
using AgentCore.AspNetCore.Voice.Session;
using AgentCore.AspNetCore.Voice.Speech;
using AgentCore.AspNetCore.Voice.Speech.Replies;
using AgentCore.AspNetCore.Voice.Turns;
using AgentCore.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Voice
{
    /// <summary>What a final prompt does to the speech the caller talked over, in LiveKit's order.</summary>
    public sealed class FinalTranscriptInterruptTests : IAsyncDisposable
    {
        private readonly FakeTimeProvider _time = new(DateTimeOffset.UnixEpoch);

        private readonly FakeConversationOutput _output = new();

        private readonly ScriptedVoicePort _port = new();

        private readonly VoiceSession _session;

        private readonly VoiceActivity _activity;

        private readonly UserTurnHandler _handler;

        public FinalTranscriptInterruptTests()
        {
            _session = new VoiceSession(_output, _time, NullLogger.Instance);
            _activity = new VoiceActivity(_session, _port, CancellationToken.None);
            _handler = new UserTurnHandler(_session, _activity);
        }

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        public ValueTask DisposeAsync()
        {
            return _output.DisposeAsync();
        }

        // agent_activity.py:2552 before 2433: on_final_transcript interrupts the current speech before on_end_of_speech
        // sets the silence a held step waits on, so the step the caller's speech held never goes out.
        [Fact(Timeout = 30_000)]
        public async Task AFinalTranscript_InterruptsAStepTheCallersSpeechHeldBeforeItCanSpeak()
        {
            SpeechHandle reply = _activity.GenerateReply("Where is my order?");
            ScriptedVoiceTurn turn = _port.Turn(1);
            await turn.TextAsync("Let me check.");
            await turn.CallAsync("1", "lookup_order");
            await Poll.UntilAsync(() => _session.HasBackgroundSpeeches && _session.Scheduler.CurrentSpeech is null);

            _handler.OnInterimTranscript();
            await turn.ResultAsync("1");
            await turn.TextAsync("Your order ships today.");
            turn.End();
            await Poll.UntilAsync(() => _session.Scheduler.CurrentSpeech == reply);

            // The user turn waits behind an earlier one, so only the final prompt's own interrupt can stop the step.
            TaskCompletionSource earlier = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _session.Scheduler.SetUserTurnTask(earlier.Task);
            _handler.OnFinalTranscript("Never mind.");
            _ = await reply;

            Assert.True(reply.IsInterrupted);
            Assert.Equal(["Let me check."], _output.Spoken);
            Assert.Equal([(0, new TurnCut("Let me check.", null))], _port.Cuts);

            _ = earlier.TrySetResult();
            Assert.Equal("Never mind.", await _port.Turn(2).Started);
            _port.Turn(2).End();
            await _session.WaitForIdleAsync(Ct);
        }

        // generation.py:748-768, and a cut keeps exactly the heard text: a final prompt cuts a
        // step mid-way, which stops the turn at once with the text forwarded; the reply then waits for the
        // transport's report of what was heard, recuts its turn to it, and only then does the next reply's turn start.
        [Fact(Timeout = 30_000)]
        public async Task AFinalTranscriptMidStep_CutsTheTurnAtOnce_ThenRecutsItToTheHeardTextThatFollows()
        {
            SpeechHandle reply = _activity.GenerateReply("hi");
            ScriptedVoiceTurn turn = _port.Turn(1);
            await turn.TextAsync("Hello there, ");
            await _output.WaitForLogAsync(1);

            _handler.OnFinalTranscript("wait");
            _ = await Task.WhenAny(
                reply.WaitForPlayoutAsync(Ct),
                _time.WaitForTimersAsync(_time.GetUtcNow() + VoiceOptions.DefaultHeardTextWait, 1));
            Assert.Equal([(0, new TurnCut("Hello there, ", null))], _port.Cuts);
            Assert.False(_port.Turn(2).Started.IsCompleted);

            await _activity.InterruptByAudioActivity("Hello", TimeSpan.FromMilliseconds(400));
            _ = await reply;

            Assert.Equal([(0, new TurnCut("Hello", TimeSpan.FromMilliseconds(400)))], _port.Recuts);
            Assert.Equal("wait", await _port.Turn(2).Started);
            _port.Turn(2).End();
            await _session.WaitForIdleAsync(Ct);
        }

        // The reply was all sent and its speech done when the final prompt came; the report that follows
        // the prompt inside the wait cuts the line and the turn alike.
        [Fact(Timeout = 30_000)]
        public async Task AReportInsideTheWaitAFinalPromptLeftOpen_CutsTheLineAndTheTurn()
        {
            (VoiceActivity activity, UserTurnHandler handler, Func<ReplyHearing> line, _) = WithLines();
            await SpokenWholeAsync(activity, "hello there caller");

            handler.OnFinalTranscript("thanks");
            await _time.WaitForTimersAsync(_time.GetUtcNow() + VoiceOptions.DefaultHeardTextWait, 1);
            await activity.InterruptByAudioActivity("hello there", TimeSpan.FromMilliseconds(400));

            Assert.Equal("hello there", await line().HeardWhenSettledAsync());
            Assert.Equal([(0, new TurnCut("hello there", TimeSpan.FromMilliseconds(400)))], _port.Cuts);
            _port.Turn(2).End();
        }

        // A report that lands once the wait is over changes neither the line nor the turn, and is logged.
        [Fact(Timeout = 30_000)]
        public async Task AReportAfterTheWaitAFinalPromptLeftOpen_ChangesNeitherTheLineNorTheTurn()
        {
            (VoiceActivity activity, UserTurnHandler handler, Func<ReplyHearing> line, RecordingLoggerFactory logs) = WithLines();
            await SpokenWholeAsync(activity, "hello there caller");

            handler.OnFinalTranscript("thanks");
            await _time.WaitForTimersAsync(_time.GetUtcNow() + VoiceOptions.DefaultHeardTextWait, 1);
            _time.Advance(VoiceOptions.DefaultHeardTextWait);
            Assert.Equal("hello there caller", await line().HeardWhenSettledAsync());
            await activity.InterruptByAudioActivity("hello there", TimeSpan.FromMilliseconds(400));

            Assert.Equal("hello there caller", line().HeardText);
            Assert.Empty(_port.Cuts);
            _ = Assert.Single(logs.Of(37));
            _port.Turn(2).End();
        }

        // agent_activity.py:2788-2796: scheduling paused by a close while the user turn waited for the speech it
        // interrupted, so the turn is skipped: no engine turn starts, and the user-turn task ends without a fault.
        [Fact(Timeout = 30_000)]
        public async Task AFinalTranscriptWhoseSpeechEndsAfterAClosePausedScheduling_StartsNoTurnAndFaultsNothing()
        {
            RecordingLoggerFactory logs = new();
            VoiceSession session = new(_output, _time, logs.CreateLogger("voice"));
            VoiceActivity activity = new(session, _port, CancellationToken.None);
            UserTurnHandler handler = new(session, activity);
            SpeechHandle reply = activity.GenerateReply("hi");
            await _port.Turn(1).TextAsync("Hello there, ");
            await _output.WaitForLogAsync(1);

            handler.OnFinalTranscript("wait");
            Task turn = session.Scheduler.UserTurnTask!;
            await _time.WaitForTimersAsync(_time.GetUtcNow() + VoiceOptions.DefaultHeardTextWait, 1);
            Task pausing = session.Scheduler.PauseSchedulingAsync();
            _time.Advance(VoiceOptions.DefaultHeardTextWait);

            await turn;
            await pausing;

            Assert.True(reply.IsDone);
            _ = Assert.Single(logs.Of(31));
            Assert.Empty(logs.Of(28));
            Assert.False(_port.Turn(2).Started.IsCompleted);
        }

        // A session of its own, logged, whose activity hands out what the caller heard of its first reply.
        private (VoiceActivity Activity, UserTurnHandler Handler, Func<ReplyHearing> Line, RecordingLoggerFactory Logs) WithLines()
        {
            RecordingLoggerFactory logs = new();
            VoiceSession session = new(_output, _time, logs.CreateLogger("voice"));
            ReplyHearing? line = null;
            VoiceActivity activity = new(session, _port, CancellationToken.None, agentLine: hearing => line ??= hearing);
            return (activity, new UserTurnHandler(session, activity), () => line!, logs);
        }

        private async Task SpokenWholeAsync(VoiceActivity activity, string text)
        {
            SpeechHandle reply = activity.GenerateReply("hi");
            ScriptedVoiceTurn turn = _port.Turn(1);
            await turn.TextAsync(text);
            turn.End();
            _ = await reply;
        }
    }
}
