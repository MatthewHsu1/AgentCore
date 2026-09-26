// Portions derived from LiveKit Agents, tests/test_speech_handle_exception.py,
// tests/test_interrupt_protected_speech.py and tests/test_coverage_spans.py,
// commit d8405f132e1bd960f298190c18daf81ffc1faf45. Copyright 2023 LiveKit, Inc.
// Licensed under the Apache License, Version 2.0. Modified: translated to C#.

using AgentCore.AspNetCore.Voice;
using AgentCore.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Voice
{
    /// <summary>Error reporting, interruption and the 5 s backstop of <see cref="SpeechHandle"/>.</summary>
    public sealed class SpeechHandleTests
    {
        private const int SpeechNotDoneAfterInterruption = 24;

        private const int SpeechDoneCallbackFaulted = 25;

        private readonly FakeTimeProvider _time = new(DateTimeOffset.UnixEpoch);

        // test_speech_handle_exception.py::test_await_does_not_raise_on_error
        [Fact(Timeout = 30_000)]
        public async Task AwaitingAFailedHandle_DoesNotRaise_AndErrorHoldsTheCause()
        {
            SpeechHandle handle = SpeechHandle.Create(_time, NullLogger.Instance);
            handle.MarkDone(new TimeoutException("generate_reply timed out."));

            SpeechHandle result = await handle;
            Assert.Same(handle, result);

            await handle.WaitForPlayoutAsync(TestContext.Current.CancellationToken);

            TimeoutException error = Assert.IsType<TimeoutException>(handle.Error);
            Assert.Equal("generate_reply timed out.", error.Message);
        }

        // test_speech_handle_exception.py::test_exception_is_none_without_error
        [Fact(Timeout = 30_000)]
        public async Task AHandleDoneWithoutError_HasNoError()
        {
            SpeechHandle handle = SpeechHandle.Create(_time, NullLogger.Instance);
            handle.MarkDone();

            _ = await handle;
            Assert.Null(handle.Error);
        }

        // test_speech_handle_exception.py::test_exception_raises_if_not_done
        [Fact]
        public void ReadingErrorBeforeDone_Throws()
        {
            SpeechHandle handle = SpeechHandle.Create(_time, NullLogger.Instance);

            _ = Assert.Throws<InvalidOperationException>(() => handle.Error);
        }

        // test_speech_handle_exception.py::test_error_ignored_after_done
        [Fact]
        public void TheFirstMarkDone_KeepsItsError()
        {
            SpeechHandle handle = SpeechHandle.Create(_time, NullLogger.Instance);
            handle.MarkDone(new TimeoutException("first"));
            handle.MarkDone();
            handle.MarkDone(new TimeoutException("second"));

            TimeoutException error = Assert.IsType<TimeoutException>(handle.Error);
            Assert.Equal("first", error.Message);
        }

        // test_interrupt_protected_speech.py::test_an_interrupted_protected_handle_is_left_alone
        [Fact]
        public void InterruptingAForceInterruptedProtectedHandle_IsANoOp()
        {
            SpeechHandle handle = SpeechHandle.Create(_time, NullLogger.Instance, allowInterruptions: false);
            _ = handle.Interrupt(force: true);

            Assert.Same(handle, handle.Interrupt());
            Assert.True(handle.IsInterrupted);
            handle.MarkDone();
        }

        // test_interrupt_protected_speech.py::test_a_done_protected_handle_is_left_alone
        [Fact]
        public void InterruptingADoneProtectedHandle_IsANoOp()
        {
            SpeechHandle handle = SpeechHandle.Create(_time, NullLogger.Instance, allowInterruptions: false);
            handle.MarkDone();

            Assert.Same(handle, handle.Interrupt());
            Assert.False(handle.IsInterrupted);
        }

        // test_coverage_spans.py::test_interrupt_source_first_wins
        [Fact]
        public void TheFirstInterruptionSource_Wins_AndTheDefaultIsProgrammatic()
        {
            SpeechHandle handle = SpeechHandle.Create(_time, NullLogger.Instance);
            _ = handle.Interrupt(source: InterruptionSource.AudioActivity);
            _ = handle.Interrupt(source: InterruptionSource.UserTurn);

            Assert.Equal(InterruptionSource.AudioActivity, handle.InterruptSource);
            Assert.Equal(InterruptionSource.Programmatic, SpeechHandle.Create(_time, NullLogger.Instance).Interrupt().InterruptSource);
        }

        // speech_handle.py allow_interruptions setter: RuntimeError when disabling on an interrupted handle.
        [Fact]
        public void DisallowingInterruptionsOnAnInterruptedHandle_Throws()
        {
            SpeechHandle handle = SpeechHandle.Create(_time, NullLogger.Instance);
            _ = handle.Interrupt();

            _ = Assert.Throws<InvalidOperationException>(() => handle.AllowInterruptions = false);
            handle.AllowInterruptions = true;
            Assert.True(handle.AllowInterruptions);
        }

        [Fact]
        public void WithNoGeneration_WaitingForOrMarkingOne_ThrowsInvalidOperation()
        {
            SpeechHandle handle = SpeechHandle.Create(_time, NullLogger.Instance);

            _ = Assert.Throws<InvalidOperationException>(() => { _ = handle.WaitForGenerationAsync(); });
            _ = Assert.Throws<InvalidOperationException>(handle.MarkGenerationDone);
        }

        // speech_handle.py _cancel._on_timeout: every task is cancelled, then the speech is marked done.
        [Fact]
        public void TheBackstop_CancelsTheTasksBeforeItMarksTheSpeechDone()
        {
            SpeechHandle handle = SpeechHandle.Create(_time, NullLogger.Instance);
            bool? doneWhenCancelled = null;
            using CancellationTokenRegistration registration = handle.TaskCancellationToken.Register(
                () => doneWhenCancelled = handle.IsDone);

            _ = handle.Interrupt();
            _time.Advance(SpeechHandle.InterruptionTimeout);

            Assert.False(doneWhenCancelled);
            Assert.True(handle.IsDone);
        }

        [Fact(Timeout = 30_000)]
        public void AnInterruptedSpeechStillRunningAfterFiveSeconds_IsCancelledAndMarkedDone()
        {
            RecordingLoggerFactory logs = new();
            SpeechHandle handle = SpeechHandle.Create(_time, logs.CreateLogger("speech"));
            Task ignoresTheInterruption = Task.Delay(Timeout.Infinite, handle.TaskCancellationToken);
            handle.AddTask(ignoresTheInterruption);

            _ = handle.Interrupt();
            _time.Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromTicks(1));

            Assert.False(handle.IsDone);
            Assert.False(ignoresTheInterruption.IsCompleted);

            _time.Advance(TimeSpan.FromTicks(1));

            Assert.True(handle.IsDone);
            Assert.True(ignoresTheInterruption.IsCanceled);
            CapturedLine line = Assert.Single(logs.Of(SpeechNotDoneAfterInterruption));
            Assert.Equal(LogLevel.Error, line.Level);
        }

        [Fact]
        public void ASpeechDoneBeforeTheBackstop_IsNeverCancelled()
        {
            RecordingLoggerFactory logs = new();
            SpeechHandle handle = SpeechHandle.Create(_time, logs.CreateLogger("speech"));

            _ = handle.Interrupt();
            handle.MarkDone();
            _time.Advance(TimeSpan.FromSeconds(10));

            Assert.False(handle.TaskCancellationToken.IsCancellationRequested);
            Assert.Empty(logs.Of(SpeechNotDoneAfterInterruption));
        }

        [Fact]
        public void ABackstopCallbackThatRunsAfterMarkDoneDisposedItsTimer_DoesNothing()
        {
            RecordingLoggerFactory logs = new();
            LateTimerTimeProvider time = new();
            SpeechHandle handle = SpeechHandle.Create(time, logs.CreateLogger("speech"));

            _ = handle.Interrupt();
            handle.MarkDone();
            time.FireCapturedTimer();

            Assert.False(handle.TaskCancellationToken.IsCancellationRequested);
            Assert.Empty(logs.Of(SpeechNotDoneAfterInterruption));
        }

        [Fact(Timeout = 30_000)]
        public async Task ADoneCallback_NeverRunsInsideMarkDone()
        {
            SpeechHandle handle = SpeechHandle.Create(_time, NullLogger.Instance);
            int markDoneThread = Environment.CurrentManagedThreadId;
            bool insideMarkDone = false;
            TaskCompletionSource<bool> ranInline = new(TaskCreationOptions.RunContinuationsAsynchronously);
            handle.AddDoneCallback(_ => ranInline.SetResult(
                Volatile.Read(ref insideMarkDone) && Environment.CurrentManagedThreadId == markDoneThread));

            Volatile.Write(ref insideMarkDone, true);
            handle.MarkDone();
            Volatile.Write(ref insideMarkDone, false);

            Assert.False(await ranInline.Task);
        }

        [Fact(Timeout = 30_000)]
        public async Task ADoneCallbackAddedAfterDone_StillRuns_AndNotInline()
        {
            SpeechHandle handle = SpeechHandle.Create(_time, NullLogger.Instance);
            TaskCompletionSource registeredRan = new(TaskCreationOptions.RunContinuationsAsynchronously);
            handle.AddDoneCallback(_ => registeredRan.SetResult());
            handle.MarkDone();

            // Once a registered callback ran, the callbacks registered before done have all been taken.
            await registeredRan.Task;
            int callerThread = Environment.CurrentManagedThreadId;
            bool insideAdd = false;
            TaskCompletionSource<bool> ranInline = new(TaskCreationOptions.RunContinuationsAsynchronously);

            Volatile.Write(ref insideAdd, true);
            handle.AddDoneCallback(_ => ranInline.SetResult(
                Volatile.Read(ref insideAdd) && Environment.CurrentManagedThreadId == callerThread));
            Volatile.Write(ref insideAdd, false);

            Assert.False(await ranInline.Task);
        }

        [Fact(Timeout = 30_000)]
        public async Task AThrowingDoneCallback_IsLogged_AndTheNextOneStillRuns()
        {
            RecordingLoggerFactory logs = new();
            SpeechHandle handle = SpeechHandle.Create(_time, logs.CreateLogger("speech"));
            TaskCompletionSource secondRan = new(TaskCreationOptions.RunContinuationsAsynchronously);
            handle.AddDoneCallback(_ => throw new InvalidOperationException("boom"));
            handle.AddDoneCallback(_ => secondRan.SetResult());

            handle.MarkDone();
            await secondRan.Task;

            CapturedLine line = Assert.Single(logs.Of(SpeechDoneCallbackFaulted));
            Assert.Equal(LogLevel.Warning, line.Level);
        }

        // speech_handle.py keeps its done callbacks in a set.
        [Fact(Timeout = 30_000)]
        public async Task ADoneCallbackRegisteredTwice_RunsOnce()
        {
            SpeechHandle handle = SpeechHandle.Create(_time, NullLogger.Instance);
            int runs = 0;
            Action<SpeechHandle> counted = _ => Interlocked.Increment(ref runs);
            TaskCompletionSource lastRan = new(TaskCreationOptions.RunContinuationsAsynchronously);
            handle.AddDoneCallback(counted);
            handle.AddDoneCallback(counted);
            handle.AddDoneCallback(_ => lastRan.SetResult());

            handle.MarkDone();
            await lastRan.Task;

            Assert.Equal(1, Volatile.Read(ref runs));
        }

        /// <summary>Hands out timers whose callback the test runs by hand, even after the handle disposed them.</summary>
        private sealed class LateTimerTimeProvider : TimeProvider
        {
            private TimerCallback? _callback;

            private object? _state;

            public void FireCapturedTimer()
            {
                Assert.NotNull(_callback);
                _callback(_state);
            }

            public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            {
                _callback = callback;
                _state = state;
                return new InertTimer();
            }

            private sealed class InertTimer : ITimer
            {
                public bool Change(TimeSpan dueTime, TimeSpan period)
                {
                    return true;
                }

                public void Dispose()
                {
                    // The captured callback stays runnable, as a thread-pool timer's already-queued callback does.
                }

                public ValueTask DisposeAsync()
                {
                    return ValueTask.CompletedTask;
                }
            }
        }
    }
}
