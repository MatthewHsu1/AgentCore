using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Voice;
using AgentCore.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Voice
{
    /// <summary>The away timer around close and around a callback .NET dispatched before a disarm. An
    /// unhandled throw on a <see cref="System.Threading.Timer"/> thread ends the process, so no away
    /// callback may throw, and none may act once its timer is gone (LiveKit's <c>aclose</c> disarms the
    /// timer last, <c>agent_session.py:1329</c>).</summary>
    public sealed class VoiceSessionAwayCloseTests : IAsyncDisposable
    {
        private static readonly UserAwayOptions Away = new(TimeSpan.FromSeconds(15), "Are you still there?");

        private readonly FakeConversationOutput _output = new()
        {
            BeforeSpeakReturns = (_, ct) => Task.Delay(Timeout.Infinite, ct),
        };

        public ValueTask DisposeAsync()
        {
            return _output.DisposeAsync();
        }

        [Fact(Timeout = 30_000)]
        public async Task ClosingMidSpeech_LeavesTheAwayTimerDisarmed()
        {
            FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
            VoiceSession session = new(_output, time, NullLogger.Instance, Away);
            session.Start();
            _ = session.Say("hello");
            await _output.WaitForLogAsync(1);
            await Poll.UntilAsync(() => session.AgentState == AgentState.Speaking);

            await session.CloseAsync();
            await Poll.UntilAsync(() => session.AgentState == AgentState.Listening);

            Exception? thrown = Record.Exception(() => time.Advance(TimeSpan.FromSeconds(15)));

            Assert.Null(thrown);
            Assert.Equal(UserState.Listening, session.UserState);
        }

        // Unfixed, this does not fail: it ends the test process.
        [Fact(Timeout = 30_000)]
        public async Task ClosingMidSpeech_OnTheRealClock_DoesNotEndTheProcess()
        {
            VoiceSession session = new(
                _output, TimeProvider.System, NullLogger.Instance, new UserAwayOptions(TimeSpan.FromMilliseconds(300), Away.Say));
            session.SetAgentState(AgentState.Thinking);
            _ = session.Say("hello");
            await _output.WaitForLogAsync(1);

            await session.CloseAsync();
            await Task.Delay(1500, TestContext.Current.CancellationToken);

            Assert.Equal(UserState.Listening, session.UserState);
        }

        [Fact(Timeout = 30_000)]
        public void ALateCallbackOfADisarmedTimer_LeavesASpeakingCallerSpeaking()
        {
            CapturingTimeProvider time = new();
            VoiceSession session = new(_output, time, NullLogger.Instance, Away);
            session.Start();
            CapturedTimer armed = Assert.Single(time.Timers);

            session.SetUserState(UserState.Speaking);
            armed.Fire();

            Assert.Equal(UserState.Speaking, session.UserState);
            Assert.Empty(_output.Spoken);
        }

        [Fact(Timeout = 30_000)]
        public void ALateCallbackOfAReplacedTimer_DoesNothing()
        {
            CapturingTimeProvider time = new();
            VoiceSession session = new(_output, time, NullLogger.Instance, Away);
            session.Start();
            CapturedTimer first = time.Timers[0];

            session.RefreshUserAwayOnFinalTranscript();
            first.Fire();

            Assert.Equal(2, time.Timers.Count);
            Assert.Equal(UserState.Listening, session.UserState);
        }

        // The window between the callback's closed check and its Say: scheduling already paused.
        [Fact(Timeout = 30_000)]
        public async Task AnAwayPromptThatCannotBeScheduled_IsLoggedNotThrown()
        {
            CapturingTimeProvider time = new();
            RecordingLoggerFactory logs = new();
            VoiceSession session = new(_output, time, logs.CreateLogger("voice"), Away);
            session.Start();
            await session.Scheduler.PauseSchedulingAsync();

            Exception? thrown = Record.Exception(time.Timers[0].Fire);

            Assert.Null(thrown);
            Assert.Equal(UserState.Away, session.UserState);
            _ = Assert.Single(logs.Of(33));
        }

        [Fact(Timeout = 30_000)]
        public async Task AFinalTranscriptWhileAway_BringsTheCallerBackToListening()
        {
            FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
            FakeConversationOutput output = new();
            VoiceSession session = new(output, time, NullLogger.Instance, Away);
            session.Start();
            time.Advance(Away.Timeout);
            await Poll.UntilAsync(() => session.UserState == UserState.Away);

            session.RefreshUserAwayOnFinalTranscript();

            Assert.Equal(UserState.Listening, session.UserState);
            await output.DisposeAsync();
        }
    }
}
