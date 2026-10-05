using AgentCore.AspNetCore.Voice.Speech;
using AgentCore.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Voice
{
    /// <summary>The play order of <see cref="SpeechQueue"/>: LiveKit's heap on <c>(-priority, perf_counter_ns)</c>.</summary>
    public sealed class SpeechQueueTests
    {
        [Fact]
        public void Dequeue_TakesTheHighestPriorityFirst_AndTheFirstQueuedWithinAPriority()
        {
            FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
            SpeechHandle low = SpeechHandle.Create(time, NullLogger.Instance);
            SpeechHandle firstHigh = SpeechHandle.Create(time, NullLogger.Instance);
            SpeechHandle normal = SpeechHandle.Create(time, NullLogger.Instance);
            SpeechHandle secondHigh = SpeechHandle.Create(time, NullLogger.Instance);
            SpeechQueue queue = new();
            queue.Enqueue(low, SpeechPriority.Low);
            queue.Enqueue(firstHigh, SpeechPriority.High);
            queue.Enqueue(normal, SpeechPriority.Normal);
            queue.Enqueue(secondHigh, SpeechPriority.High);

            List<SpeechHandle> played = [];
            while (queue.TryDequeue(out SpeechHandle? speech))
            {
                played.Add(speech);
            }

            Assert.Equal([firstHigh, secondHigh, normal, low], played);
        }

        [Fact]
        public void Dequeue_KeepsQueueOrderAcrossManySpeechesOfOnePriority()
        {
            SpeechQueue queue = new();
            SpeechHandle[] queued = [.. Enumerable.Range(0, 5).Select(_ => Speech())];
            foreach (SpeechHandle speech in queued)
            {
                queue.Enqueue(speech, SpeechPriority.Normal);
            }

            List<SpeechHandle> played = [];
            while (queue.TryDequeue(out SpeechHandle? speech))
            {
                played.Add(speech);
            }

            Assert.Equal(queued, played);
        }

        // agent_activity.py interrupt: sorted(self._speech_q), because the heap's list order is not play order.
        [Fact]
        public void SortedSnapshot_ListsThePlayOrder()
        {
            SpeechHandle normal = Speech();
            SpeechHandle firstHigh = Speech();
            SpeechHandle secondHigh = Speech();
            SpeechQueue queue = new();
            queue.Enqueue(normal, SpeechPriority.Normal);
            queue.Enqueue(firstHigh, SpeechPriority.High);
            queue.Enqueue(secondHigh, SpeechPriority.High);

            Assert.Equal([firstHigh, secondHigh, normal], queue.SortedSnapshot());
        }

        private static SpeechHandle Speech()
        {
            return SpeechHandle.Create(new FakeTimeProvider(DateTimeOffset.UnixEpoch), NullLogger.Instance);
        }
    }
}
