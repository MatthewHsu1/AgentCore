// Portions derived from LiveKit Agents, tests/test_filler.py (test_scheduler_can_start_and_stop_without_firing
// :118, test_scheduler_stop_is_idempotent :138, test_scheduler_fires_after_idle_dwell :156,
// test_scheduler_does_not_fire_before_dwell_elapses :175, test_scheduler_dwell_resets_on_agent_state_change
// :194, test_scheduler_dwell_resets_on_user_speaking :222, test_scheduler_invokes_callable_source_lazily
// :245, test_scheduler_fires_repeatedly_with_interval :274, test_scheduler_interval_none_fires_once_only
// :295, test_scheduler_does_not_fire_after_speech_interrupted :357, test_scheduler_reports_a_say_that_raises
// :447), commit d8405f132e1bd960f298190c18daf81ffc1faf45. Copyright 2023 LiveKit, Inc.
// Licensed under the Apache License, Version 2.0. Modified: translated to C#, over a real VoiceSession and
// a FakeTimeProvider rather than a hand-rolled fake session and real wall-clock sleeps. LiveKit's own
// suite runs these under an autojumping virtual clock (tests/virtual_time.py) that advances only once the
// loop is otherwise idle. Here each test first waits until the loop has armed the timer it is about to
// pass (FakeTimeProvider.WaitForTimersAsync), then advances the clock.

using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Voice.Filler;
using AgentCore.AspNetCore.Voice.Session;
using AgentCore.AspNetCore.Voice.Speech;
using AgentCore.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Voice
{
    /// <summary><see cref="FillerScheduler"/>'s idle dwell, its reset on a state change, its interval and
    /// step cap, and how it stops when the speech it serves is interrupted or it faults.</summary>
    public sealed class FillerSchedulerTests : IAsyncDisposable
    {
        private const int FillerFaulted = 32;

        private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;

        private readonly FakeTimeProvider _time = new(Start);

        private readonly FakeConversationOutput _output = new();

        private readonly VoiceSession _session;

        private readonly SpeechHandle _handle;

        public FillerSchedulerTests()
        {
            _session = new VoiceSession(_output, _time, NullLogger.Instance);
            _handle = SpeechHandle.Create(_time, NullLogger.Instance);
        }

        public ValueTask DisposeAsync()
        {
            return _output.DisposeAsync();
        }

        [Fact(Timeout = 30_000)]
        public async Task AScheduler_StoppedBeforeTheDwellElapses_FiresNothing()
        {
            FillerScheduler scheduler = new(_session, _handle, "should not fire", TimeSpan.FromSeconds(10));
            await ArmedAsync(Start.AddSeconds(10));

            await scheduler.CloseAsync();

            Assert.True(_time.WaitForTimersAsync(Start.AddSeconds(10), 0).IsCompleted);
            Assert.Empty(_output.Spoken);
        }

        [Fact(Timeout = 30_000)]
        public async Task Close_IsIdempotent()
        {
            FillerScheduler scheduler = new(_session, _handle, "x", TimeSpan.FromSeconds(10));

            await scheduler.CloseAsync();
            Exception? second = await Record.ExceptionAsync(scheduler.CloseAsync);

            Assert.Null(second);
        }

        // filler_scheduler.py (_FillerScheduler.__init__): a negative dwell or interval is refused.
        [Fact]
        public void ANegativeDelayOrInterval_IsRefused()
        {
            _ = Assert.Throws<ArgumentOutOfRangeException>(
                () => new FillerScheduler(_session, _handle, "x", TimeSpan.FromSeconds(-1)));
            _ = Assert.Throws<ArgumentOutOfRangeException>(
                () => new FillerScheduler(_session, _handle, "x", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(-1)));
        }

        [Fact(Timeout = 30_000)]
        public async Task ItFires_OnceTheSessionHasBeenIdleForTheDwell()
        {
            FillerScheduler scheduler = new(_session, _handle, "let me check", TimeSpan.FromMilliseconds(50));
            await ArmedAsync(Start.AddMilliseconds(50));

            _time.Advance(TimeSpan.FromMilliseconds(50));
            await _output.WaitForLogAsync(2);
            await scheduler.CloseAsync();

            Assert.Equal(["let me check"], _output.Spoken);
        }

        [Fact(Timeout = 30_000)]
        public async Task ItNeverFires_BeforeTheDwellElapses()
        {
            FillerScheduler scheduler = new(_session, _handle, "let me check", TimeSpan.FromSeconds(1));
            await ArmedAsync(Start.AddSeconds(1));

            _time.Advance(TimeSpan.FromMilliseconds(50));
            await scheduler.CloseAsync();

            Assert.Empty(_output.Spoken);
        }

        [Fact(Timeout = 30_000)]
        public async Task AnAgentStateChangeMidDwell_RestartsIt()
        {
            FillerScheduler scheduler = new(_session, _handle, "ping", TimeSpan.FromMilliseconds(100));
            await ArmedAsync(Start.AddMilliseconds(100));
            _time.Advance(TimeSpan.FromMilliseconds(50));

            _session.SetAgentState(AgentState.Speaking);
            await ArmedAsync(Start.AddMilliseconds(150));

            _time.Advance(TimeSpan.FromMilliseconds(80));
            Assert.Empty(_output.Spoken);

            _time.Advance(TimeSpan.FromMilliseconds(20));
            await _output.WaitForLogAsync(2);
            await scheduler.CloseAsync();

            Assert.Equal(["ping"], _output.Spoken);
        }

        [Fact(Timeout = 30_000)]
        public async Task AnAgentThinkingMidDwell_RestartsIt()
        {
            FillerScheduler scheduler = new(_session, _handle, "ping", TimeSpan.FromMilliseconds(100));
            await ArmedAsync(Start.AddMilliseconds(100));
            _time.Advance(TimeSpan.FromMilliseconds(50));

            _session.SetAgentState(AgentState.Thinking);
            await ArmedAsync(Start.AddMilliseconds(150));
            await scheduler.CloseAsync();

            Assert.Empty(_output.Spoken);
        }

        [Fact(Timeout = 30_000)]
        public async Task UserStateSpeakingMidDwell_RestartsIt()
        {
            FillerScheduler scheduler = new(_session, _handle, "ping", TimeSpan.FromMilliseconds(100));
            await ArmedAsync(Start.AddMilliseconds(100));
            _time.Advance(TimeSpan.FromMilliseconds(50));

            _session.SetUserState(UserState.Speaking);
            await ArmedAsync(Start.AddMilliseconds(150));

            _time.Advance(TimeSpan.FromMilliseconds(80));
            Assert.Empty(_output.Spoken);

            _time.Advance(TimeSpan.FromMilliseconds(20));
            await _output.WaitForLogAsync(2);
            await scheduler.CloseAsync();

            Assert.Equal(["ping"], _output.Spoken);
        }

        [Fact(Timeout = 30_000)]
        public async Task ACallableSource_IsInvokedLazilyAtFireTime()
        {
            List<int> invocations = [];
            FillerSource source = FillerSource.FromCallable(step =>
            {
                invocations.Add(step);
                return _session.Say("from callable");
            });
            FillerScheduler scheduler = new(_session, _handle, source, TimeSpan.FromMilliseconds(50));
            await ArmedAsync(Start.AddMilliseconds(50));

            Assert.Empty(invocations);
            _time.Advance(TimeSpan.FromMilliseconds(50));
            await _output.WaitForLogAsync(2);
            await scheduler.CloseAsync();

            Assert.Equal([0], invocations);
            Assert.Equal(["from callable"], _output.Spoken);
        }

        // filler_scheduler.py:84-90: a callable that returns nothing skips its fire.
        [Fact(Timeout = 30_000)]
        public async Task ACallableSourceReturningNothing_SkipsThatFire()
        {
            FillerScheduler scheduler = new(
                _session, _handle, FillerSource.FromCallable(static _ => null), TimeSpan.FromMilliseconds(50));
            await ArmedAsync(Start.AddMilliseconds(50));

            _time.Advance(TimeSpan.FromMilliseconds(50));
            await scheduler.CloseAsync();

            Assert.Empty(scheduler.CreatedSpeeches);
        }

        [Fact(Timeout = 30_000)]
        public async Task AnIntervalSet_FiresAgainAfterEachCooldown()
        {
            FillerScheduler scheduler = new(
                _session, _handle, "tick", TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50));

            for (int fire = 1; fire <= 3; fire++)
            {
                DateTimeOffset dwellEnds = Start.AddMilliseconds((100 * fire) - 50);
                await ArmedAsync(dwellEnds);
                _time.Advance(TimeSpan.FromMilliseconds(50));
                await _output.WaitForLogAsync(2 * fire);

                await ArmedAsync(dwellEnds.AddMilliseconds(50));
                Assert.Equal(fire, _output.Spoken.Count);
                _time.Advance(TimeSpan.FromMilliseconds(50));
            }

            await scheduler.CloseAsync();

            Assert.Equal(["tick", "tick", "tick"], _output.Spoken);
        }

        [Fact(Timeout = 30_000)]
        public async Task AStepCap_StopsFiringOnceReached()
        {
            FillerScheduler scheduler = new(
                _session, _handle, "tick", TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50), maxSteps: 1);
            await ArmedAsync(Start.AddMilliseconds(50));

            _time.Advance(TimeSpan.FromMilliseconds(50));
            await _output.WaitForLogAsync(2);

            Assert.True(_time.WaitForTimersAsync(Start.AddMilliseconds(100), 0).IsCompleted, "a cooldown was armed past the cap.");
            await scheduler.CloseAsync();
            Assert.Equal(["tick"], _output.Spoken);
        }

        [Fact(Timeout = 30_000)]
        public async Task NoInterval_FiresAtMostOnce()
        {
            FillerScheduler scheduler = new(_session, _handle, "once", TimeSpan.FromMilliseconds(20));
            await ArmedAsync(Start.AddMilliseconds(20));

            _time.Advance(TimeSpan.FromMilliseconds(20));
            await _output.WaitForLogAsync(2);
            _time.Advance(TimeSpan.FromSeconds(1));
            await scheduler.CloseAsync();

            Assert.Equal(["once"], _output.Spoken);
        }

        [Fact(Timeout = 30_000)]
        public async Task TheSpeechBeingInterrupted_StopsFurtherFiring()
        {
            FillerScheduler scheduler = new(
                _session, _handle, "x", TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20));
            await ArmedAsync(Start.AddMilliseconds(20));
            _time.Advance(TimeSpan.FromMilliseconds(20));
            await _output.WaitForLogAsync(2);
            await ArmedAsync(Start.AddMilliseconds(40));

            _ = _handle.Interrupt();

            await _time.WaitForTimersAsync(Start.AddMilliseconds(40), 0);
            _time.Advance(TimeSpan.FromMilliseconds(200));
            await scheduler.CloseAsync();
            Assert.Equal(["x"], _output.Spoken);
        }

        // A source that raises is reported: nothing else would tell the caller the filler died on its first fire.
        [Fact(Timeout = 30_000)]
        public async Task ASourceThatRaises_IsLoggedAndStopsTheLoop()
        {
            RecordingLoggerFactory logs = new();
            VoiceSession loggedSession = new(_output, _time, logs.CreateLogger("voice"));
            FillerSource source = FillerSource.FromCallable(static _ =>
                throw new InvalidOperationException("no TTS model and the RealtimeSession does not support say()"));

            FillerScheduler scheduler = new(loggedSession, _handle, source, TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(10), 3);
            await ArmedAsync(Start.AddMilliseconds(20));
            _time.Advance(TimeSpan.FromMilliseconds(20));
            await Poll.UntilAsync(() => logs.Of(FillerFaulted).Count >= 1);
            await scheduler.CloseAsync();

            CapturedLine line = Assert.Single(logs.Of(FillerFaulted));
            Assert.Equal(LogLevel.Error, line.Level);
            _ = Assert.IsType<InvalidOperationException>(line.Exception);
            Assert.Contains("does not support say()", line.Exception!.Message, StringComparison.Ordinal);
        }

        /// <summary>Waits until the loop has armed the one timer due at <paramref name="dueAt"/>.</summary>
        private Task ArmedAsync(DateTimeOffset dueAt)
        {
            return _time.WaitForTimersAsync(dueAt, 1);
        }
    }
}
