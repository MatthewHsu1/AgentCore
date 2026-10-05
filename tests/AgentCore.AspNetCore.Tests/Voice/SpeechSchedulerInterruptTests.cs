// Portions derived from LiveKit Agents, tests/test_interrupt_protected_speech.py,
// commit d8405f132e1bd960f298190c18daf81ffc1faf45. Copyright 2023 LiveKit, Inc.
// Licensed under the Apache License, Version 2.0. Modified: translated to C#; realtime session removed.

using AgentCore.AspNetCore.Voice.Speech;
using AgentCore.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Voice
{
    /// <summary><see cref="SpeechScheduler.Interrupt"/> against speeches that disallow interruptions.</summary>
    public sealed class SpeechSchedulerInterruptTests
    {
        private const int QueuedSpeechNotInterruptible = 26;

        private readonly FakeTimeProvider _time = new(DateTimeOffset.UnixEpoch);

        // test_interrupt_protected_speech.py::TestInterruptQueuedSpeeches::test_queue_stops_at_the_protected_speech
        [Fact(Timeout = 30_000)]
        public async Task TheQueueWalk_StopsAtTheProtectedSpeech()
        {
            using RecordingLoggerFactory logs = new();
            SpeechScheduler scheduler = new(new Lock(), logs.CreateLogger("scheduler"));
            SpeechHandle current = await PlayAsync(scheduler, Speech(allowInterruptions: true));
            SpeechHandle first = Queue(scheduler, Speech(allowInterruptions: true));
            SpeechHandle protectedSpeech = Queue(scheduler, Speech(allowInterruptions: false));
            SpeechHandle behind = Queue(scheduler, Speech(allowInterruptions: true));

            _ = scheduler.Interrupt();

            Assert.True(current.IsInterrupted);
            Assert.True(first.IsInterrupted);
            Assert.False(protectedSpeech.IsInterrupted);
            Assert.False(behind.IsInterrupted);
            Assert.Contains(logs.Of(QueuedSpeechNotInterruptible), line => line.Message.Contains("force=True", StringComparison.Ordinal));

            await StopAsync(scheduler, current, first, protectedSpeech, behind);
        }

        // test_interrupt_protected_speech.py::TestInterruptQueuedSpeeches::test_a_protected_head_shields_the_whole_queue
        [Fact]
        public void AProtectedHead_ShieldsTheWholeQueue()
        {
            SpeechScheduler scheduler = NewScheduler();
            SpeechHandle head = Queue(scheduler, Speech(allowInterruptions: false));
            SpeechHandle middle = Queue(scheduler, Speech(allowInterruptions: true));
            SpeechHandle tail = Queue(scheduler, Speech(allowInterruptions: false));

            _ = scheduler.Interrupt();

            Assert.False(head.IsInterrupted);
            Assert.False(middle.IsInterrupted);
            Assert.False(tail.IsInterrupted);
        }

        // test_interrupt_protected_speech.py::TestInterruptQueuedSpeeches::test_a_cancelled_protected_speech_does_not_shield_the_queue
        [Fact]
        public void ACancelledProtectedSpeech_DoesNotShieldTheQueue()
        {
            SpeechScheduler scheduler = NewScheduler();
            SpeechHandle protectedSpeech = Queue(scheduler, Speech(allowInterruptions: false));
            SpeechHandle behind = Queue(scheduler, Speech(allowInterruptions: true));
            _ = protectedSpeech.Interrupt(force: true);

            _ = scheduler.Interrupt();

            Assert.True(behind.IsInterrupted);
        }

        // test_interrupt_protected_speech.py::TestInterruptQueuedSpeeches::test_the_queue_is_walked_in_playout_order_not_heap_order
        [Fact]
        public void TheQueue_IsWalkedInPlayoutOrderNotHeapOrder()
        {
            SpeechScheduler scheduler = NewScheduler();
            SpeechHandle low = Queue(scheduler, Speech(allowInterruptions: true), SpeechPriority.Normal);
            SpeechHandle urgentProtected = Queue(scheduler, Speech(allowInterruptions: false), SpeechPriority.High);

            _ = scheduler.Interrupt();

            Assert.False(urgentProtected.IsInterrupted);
            Assert.False(low.IsInterrupted);
        }

        // test_interrupt_protected_speech.py::TestInterruptQueuedSpeeches::test_a_protected_playing_speech_still_raises
        [Fact(Timeout = 30_000)]
        public async Task AProtectedPlayingSpeech_StillThrows()
        {
            SpeechScheduler scheduler = NewScheduler();
            SpeechHandle current = await PlayAsync(scheduler, Speech(allowInterruptions: false));

            _ = Assert.Throws<InvalidOperationException>(() => { _ = scheduler.Interrupt(); });

            await StopAsync(scheduler, current);
        }

        // test_interrupt_protected_speech.py::TestInterruptQueuedSpeeches::test_force_interrupts_the_whole_chain
        [Fact(Timeout = 30_000)]
        public async Task Force_InterruptsTheWholeChain()
        {
            SpeechScheduler scheduler = NewScheduler();
            SpeechHandle current = await PlayAsync(scheduler, Speech(allowInterruptions: false));
            SpeechHandle queued = Queue(scheduler, Speech(allowInterruptions: false));
            SpeechHandle behind = Queue(scheduler, Speech(allowInterruptions: true));

            _ = scheduler.Interrupt(force: true);

            Assert.True(current.IsInterrupted);
            Assert.True(queued.IsInterrupted);
            Assert.True(behind.IsInterrupted);

            await StopAsync(scheduler, current, queued, behind);
        }

        // test_interrupt_protected_speech.py::TestInterruptQueuedSpeeches::test_interruptible_chain_is_unaffected
        [Fact(Timeout = 30_000)]
        public async Task AnInterruptibleChain_IsInterruptedWhole()
        {
            SpeechScheduler scheduler = NewScheduler();
            SpeechHandle current = await PlayAsync(scheduler, Speech(allowInterruptions: true));
            SpeechHandle queued = Queue(scheduler, Speech(allowInterruptions: true));

            _ = scheduler.Interrupt();

            Assert.True(current.IsInterrupted);
            Assert.True(queued.IsInterrupted);

            await StopAsync(scheduler, current, queued);
        }

        [Fact(Timeout = 30_000)]
        public async Task TheInterruptTask_CompletesOnlyWhenEveryInterruptedSpeechIsDone()
        {
            SpeechScheduler scheduler = NewScheduler();
            SpeechHandle current = await PlayAsync(scheduler, Speech(allowInterruptions: true));
            SpeechHandle queued = Queue(scheduler, Speech(allowInterruptions: true));

            Task interrupted = scheduler.Interrupt();
            TaskCompletionSource queuedCallbacksRan = new(TaskCreationOptions.RunContinuationsAsynchronously);
            queued.AddDoneCallback(_ => queuedCallbacksRan.SetResult());
            queued.MarkDone();

            // Done callbacks run in the order they were added, so the scheduler's own has run by now.
            await queuedCallbacksRan.Task;
            Assert.False(interrupted.IsCompleted);

            current.MarkDone();
            await interrupted;

            await StopAsync(scheduler);
        }

        // agent_activity.py _interrupt_background_speeches: only speeches that allow it, unless forced.
        [Fact]
        public void InterruptingBackgroundSpeeches_SkipsAProtectedOne_UnlessForced()
        {
            SpeechScheduler scheduler = NewScheduler();
            SpeechHandle open = Speech(allowInterruptions: true);
            SpeechHandle protectedSpeech = Speech(allowInterruptions: false);
            scheduler.AddBackgroundSpeech(open);
            scheduler.AddBackgroundSpeech(protectedSpeech);

            Assert.Equal([open], scheduler.InterruptBackgroundSpeeches());
            Assert.False(protectedSpeech.IsInterrupted);

            Assert.Contains(protectedSpeech, scheduler.InterruptBackgroundSpeeches(force: true));
            Assert.True(protectedSpeech.IsInterrupted);
        }

        // agent_activity.py interrupt: the background speeches are interrupted first.
        [Fact]
        public void Interrupt_AlsoInterruptsTheBackgroundSpeeches()
        {
            SpeechScheduler scheduler = NewScheduler();
            SpeechHandle background = Speech(allowInterruptions: true);
            scheduler.AddBackgroundSpeech(background);

            _ = scheduler.Interrupt();

            Assert.True(background.IsInterrupted);
        }

        private static SpeechHandle Queue(SpeechScheduler scheduler, SpeechHandle speech, SpeechPriority priority = SpeechPriority.Low)
        {
            // force: a new scheduler is paused, and LiveKit's test pushes onto the heap with no loop running.
            scheduler.ScheduleSpeech(speech, priority, force: true);
            return speech;
        }

        private static async Task<SpeechHandle> PlayAsync(SpeechScheduler scheduler, SpeechHandle speech)
        {
            scheduler.ResumeScheduling();
            scheduler.ScheduleSpeech(speech, SpeechPriority.Low);
            await speech.WaitForAuthorizationAsync(TestContext.Current.CancellationToken);
            Assert.Same(speech, scheduler.CurrentSpeech);
            return speech;
        }

        private static async Task StopAsync(SpeechScheduler scheduler, params SpeechHandle[] speeches)
        {
            foreach (SpeechHandle speech in speeches)
            {
                speech.MarkDone();
            }

            await scheduler.PauseSchedulingAsync();
        }

        private static SpeechScheduler NewScheduler()
        {
            return new SpeechScheduler(new Lock(), NullLogger.Instance);
        }

        private SpeechHandle Speech(bool allowInterruptions)
        {
            return SpeechHandle.Create(_time, NullLogger.Instance, allowInterruptions);
        }
    }
}
