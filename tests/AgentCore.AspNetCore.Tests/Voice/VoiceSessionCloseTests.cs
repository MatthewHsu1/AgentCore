using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Voice.Session;
using AgentCore.AspNetCore.Voice.Speech;
using AgentCore.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Voice
{
    /// <summary><see cref="VoiceSession.CloseAsync"/>: LiveKit's <c>_teardown_activity</c> without drain
    /// (<c>agent_session.py:1349-1386</c>) closes the session, force-interrupts every speech and stops
    /// scheduling.</summary>
    public sealed class VoiceSessionCloseTests : IAsyncDisposable
    {
        private readonly FakeTimeProvider _time = new(DateTimeOffset.UnixEpoch);

        private readonly FakeConversationOutput _output = new();

        private readonly VoiceSession _session;

        public VoiceSessionCloseTests()
        {
            _session = new VoiceSession(_output, _time, NullLogger.Instance);
        }

        public ValueTask DisposeAsync()
        {
            return _output.DisposeAsync();
        }

        [Fact(Timeout = 30_000)]
        public async Task WaitForIdle_AfterClose_Throws()
        {
            await _session.CloseAsync();

            _ = await Assert.ThrowsAsync<VoiceSessionClosedException>(
                () => _session.WaitForIdleAsync(TestContext.Current.CancellationToken));
        }

        [Fact(Timeout = 30_000)]
        public async Task WaitForIdle_MidSpeech_ThrowsWhenTheSessionClosesWithoutWaitingForTheSpeech()
        {
            TaskCompletionSource held = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _output.BeforeSpeakReturns = (_, _) => held.Task;
            SpeechHandle speech = _session.Say("hello");
            await _output.WaitForLogAsync(1);
            Task idle = _session.WaitForIdleAsync(TestContext.Current.CancellationToken);

            _session.MarkClosed();

            _ = await Assert.ThrowsAsync<VoiceSessionClosedException>(() => idle);
            Assert.False(speech.IsDone);

            _ = held.TrySetResult();
            _ = await speech;
        }

        [Fact(Timeout = 30_000)]
        public async Task Close_InterruptsASpeechThatDisallowsInterruptions()
        {
            _output.BeforeSpeakReturns = (_, cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken);
            SpeechHandle notice = _session.Say("Please hold.", allowInterruptions: false);
            await _output.WaitForLogAsync(1);

            await _session.CloseAsync();

            Assert.True(notice.IsInterrupted);
            Assert.True(notice.IsDone);
        }

        [Fact(Timeout = 30_000)]
        public async Task Say_AfterClose_IsRefused()
        {
            await _session.CloseAsync();

            _ = Assert.Throws<InvalidOperationException>(() => _session.Say("too late"));
            Assert.Empty(_output.Spoken);
        }
    }
}
