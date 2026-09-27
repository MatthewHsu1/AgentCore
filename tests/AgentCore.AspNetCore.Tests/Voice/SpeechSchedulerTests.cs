using AgentCore.AspNetCore.Voice;
using AgentCore.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Voice
{
    /// <summary>
    /// The scheduling loop, pausing and <see cref="SpeechScheduler.WaitForIdleAsync"/>, with expected values
    /// from LiveKit's <c>agent_activity.py</c> (<c>_schedule_speech</c>, <c>_scheduling_task</c>,
    /// <c>_create_speech_task</c>, <c>wait_for_idle</c>).
    /// </summary>
    public sealed class SpeechSchedulerTests
    {
        private const int SchedulingTaskNotRunning = 27;

        private readonly FakeTimeProvider _time = new(DateTimeOffset.UnixEpoch);

        private readonly Lock _sessionLock = new();

        private readonly SpeechScheduler _scheduler;

        public SpeechSchedulerTests()
        {
            _scheduler = new SpeechScheduler(_sessionLock, NullLogger.Instance);
        }

        [Fact(Timeout = 30_000)]
        public async Task TheLoop_PlaysOneSpeechAtATime_InPriorityOrder()
        {
            _scheduler.ResumeScheduling();
            SpeechHandle playing = Speech();
            _scheduler.ScheduleSpeech(playing, SpeechPriority.Normal);
            await playing.WaitForAuthorizationAsync(TestContext.Current.CancellationToken);
            SpeechHandle low = Schedule(SpeechPriority.Low);
            SpeechHandle firstHigh = Schedule(SpeechPriority.High);
            SpeechHandle normal = Schedule(SpeechPriority.Normal);
            SpeechHandle secondHigh = Schedule(SpeechPriority.High);

            foreach (SpeechHandle next in (SpeechHandle[])[firstHigh, secondHigh, normal, low])
            {
                Assert.False(next.WaitForAuthorizationAsync(TestContext.Current.CancellationToken).IsCompleted);
                _scheduler.CurrentSpeech!.MarkGenerationDone();
                await next.WaitForAuthorizationAsync(TestContext.Current.CancellationToken);
                Assert.Same(next, _scheduler.CurrentSpeech);
            }

            await StopAsync(playing, low, firstHigh, normal, secondHigh);
        }

        [Fact(Timeout = 30_000)]
        public async Task AQueuedSpeechTheBackstopMarkedDone_IsSkipped()
        {
            _scheduler.ResumeScheduling();
            SpeechHandle playing = Speech();
            _scheduler.ScheduleSpeech(playing, SpeechPriority.Normal);
            await playing.WaitForAuthorizationAsync(TestContext.Current.CancellationToken);
            SpeechHandle cut = Schedule(SpeechPriority.Normal);
            SpeechHandle next = Schedule(SpeechPriority.Normal);

            _ = cut.Interrupt();
            _time.Advance(TimeSpan.FromSeconds(5));
            Assert.True(cut.IsDone);
            playing.MarkDone();

            await next.WaitForAuthorizationAsync(TestContext.Current.CancellationToken);
            Assert.Same(next, _scheduler.CurrentSpeech);
            Assert.False(cut.HasGenerations);

            await StopAsync(playing, next);
        }

        [Fact]
        public void SchedulingWhilePaused_Throws_AndInterruptsTheSpeech()
        {
            SpeechHandle speech = Speech();

            _ = Assert.Throws<InvalidOperationException>(() => _scheduler.ScheduleSpeech(speech, SpeechPriority.Normal));

            Assert.True(speech.IsInterrupted);
            Assert.False(speech.IsScheduled);
        }

        [Fact(Timeout = 30_000)]
        public async Task ASpeech_IsMarkedDoneOnceEveryTaskLinkedToItHasEnded()
        {
            SpeechHandle speech = Speech();
            TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource releaseSecond = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task first = _scheduler.CreateSpeechTask(() => releaseFirst.Task, speech);
            _ = _scheduler.CreateSpeechTask(() => releaseSecond.Task, speech);

            releaseFirst.SetResult();
            await first;
            Assert.False(speech.IsDone);

            releaseSecond.SetResult();
            _ = await speech;
        }

        [Fact(Timeout = 30_000)]
        public async Task Pausing_WaitsForEverySpeechTaskToEnd()
        {
            _scheduler.ResumeScheduling();
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = _scheduler.CreateSpeechTask(() => release.Task);

            Task paused = _scheduler.PauseSchedulingAsync();
            Assert.False(paused.IsCompleted);

            release.SetResult();
            await paused;
        }

        [Fact(Timeout = 30_000)]
        public async Task WaitForIdle_CompletesAtOnceWithNoSpeech()
        {
            await _scheduler.WaitForIdleAsync(TestContext.Current.CancellationToken);
        }

        [Fact(Timeout = 30_000)]
        public async Task WaitForIdle_WaitsForTheCurrentAndTheQueuedSpeech()
        {
            _scheduler.ResumeScheduling();
            SpeechHandle playing = Speech();
            _scheduler.ScheduleSpeech(playing, SpeechPriority.Normal);
            await playing.WaitForAuthorizationAsync(TestContext.Current.CancellationToken);
            SpeechHandle queued = Schedule(SpeechPriority.Normal);

            Task idle = _scheduler.WaitForIdleAsync(TestContext.Current.CancellationToken);
            playing.MarkDone();
            await queued.WaitForAuthorizationAsync(TestContext.Current.CancellationToken);
            Assert.False(idle.IsCompleted);

            queued.MarkDone();
            await idle;
            Assert.Null(_scheduler.CurrentSpeech);

            await StopAsync();
        }

        [Fact(Timeout = 30_000)]
        public async Task CancellingWaitForIdle_LeavesTheSpeechPlaying()
        {
            _scheduler.ResumeScheduling();
            SpeechHandle playing = Speech();
            _scheduler.ScheduleSpeech(playing, SpeechPriority.Normal);
            await playing.WaitForAuthorizationAsync(TestContext.Current.CancellationToken);
            using CancellationTokenSource cancel = new();

            Task idle = _scheduler.WaitForIdleAsync(cancel.Token);
            await cancel.CancelAsync();

            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => idle);
            Assert.False(playing.IsDone);
            Assert.False(playing.WaitForGenerationAsync().IsCompleted);
            Assert.Same(playing, _scheduler.CurrentSpeech);

            await StopAsync(playing);
        }

        // agent_activity.py _schedule_speech: a speech scheduled once the scheduling task is done is
        // cancelled with a warning, not queued.
        [Fact(Timeout = 30_000)]
        public async Task ASpeechScheduledAfterTheLoopEnded_IsInterruptedAndNotQueued()
        {
            using RecordingLoggerFactory logs = new();
            SpeechScheduler scheduler = new(new Lock(), logs.CreateLogger("scheduler"));
            scheduler.ResumeScheduling();
            await scheduler.PauseSchedulingAsync();
            SpeechHandle late = Speech();

            scheduler.ScheduleSpeech(late, SpeechPriority.Normal, force: true);

            Assert.True(late.IsInterrupted);
            Assert.False(late.IsScheduled);
            CapturedLine line = Assert.Single(logs.Of(SchedulingTaskNotRunning));
            Assert.Equal(LogLevel.Warning, line.Level);
        }

        // agent_activity.py _scheduling_task: a speech done while queued is skipped and leaves no current speech.
        [Fact(Timeout = 30_000)]
        public async Task ASkippedDoneSpeech_LeavesNoCurrentSpeech()
        {
            SpeechHandle doneWhileQueued = Speech();
            _scheduler.ScheduleSpeech(doneWhileQueued, SpeechPriority.Normal, force: true);
            doneWhileQueued.MarkDone();

            _scheduler.ResumeScheduling();
            await _scheduler.PauseSchedulingAsync();

            Assert.Null(_scheduler.CurrentSpeech);
        }

        // agent_activity.py _on_pipeline_reply_done: a current speech that is done counts as no pending speech.
        [Fact(Timeout = 30_000)]
        public async Task ACurrentSpeechThatIsDone_IsNoPendingSpeech()
        {
            _scheduler.ResumeScheduling();
            SpeechHandle playing = Schedule(SpeechPriority.Normal);
            await playing.WaitForAuthorizationAsync(TestContext.Current.CancellationToken);

            // The loop clears the current speech under this lock, so it is still current here.
            lock (_sessionLock)
            {
                playing.MarkDone();
                Assert.Same(playing, _scheduler.CurrentSpeech);
                Assert.True(_scheduler.NoPendingSpeech);
            }

            await StopAsync();
        }

        [Fact(Timeout = 30_000)]
        public async Task WaitForIdle_WaitsForASpeechQueuedButNotYetCurrent()
        {
            SpeechHandle queued = Speech();
            _scheduler.ScheduleSpeech(queued, SpeechPriority.Normal, force: true);

            Task idle = _scheduler.WaitForIdleAsync(TestContext.Current.CancellationToken);
            Assert.False(idle.IsCompleted);

            queued.MarkDone();
            _scheduler.ResumeScheduling();
            await idle;
            await StopAsync();
        }

        [Fact(Timeout = 30_000)]
        public async Task CancellingWaitForIdle_WhileASpeechIsQueuedButNotCurrent_EndsTheWait()
        {
            SpeechHandle queued = Speech();
            _scheduler.ScheduleSpeech(queued, SpeechPriority.Normal, force: true);
            using CancellationTokenSource cancel = new();
            Task idle = _scheduler.WaitForIdleAsync(cancel.Token);

            await cancel.CancelAsync();
            queued.MarkDone();
            _scheduler.ResumeScheduling();

            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => idle);
            await StopAsync();
        }

        private SpeechHandle Speech()
        {
            return SpeechHandle.Create(_time, NullLogger.Instance);
        }

        private SpeechHandle Schedule(SpeechPriority priority)
        {
            SpeechHandle speech = Speech();
            _scheduler.ScheduleSpeech(speech, priority);
            return speech;
        }

        private async Task StopAsync(params SpeechHandle[] speeches)
        {
            foreach (SpeechHandle speech in speeches)
            {
                speech.MarkDone();
            }

            await _scheduler.PauseSchedulingAsync();
        }
    }
}
