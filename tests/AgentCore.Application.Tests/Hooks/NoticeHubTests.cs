using System.Collections.Concurrent;
using System.Diagnostics;
using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Notices;
using AgentCore.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentCore.Application.Tests.Hooks
{
    public sealed class NoticeHubTests
    {
        private static readonly FakeTimeProvider Clock = new(DateTimeOffset.UnixEpoch);

        private static NoticeHub Hub(params AgentHook[] hooks) => new(HookTable.Build(hooks), NullLogger.Instance, TimeProvider.System);

        // Raising only enqueues, so a hook that never returns cannot hold the raiser.
        [Fact(Timeout = 10_000)]
        public async Task ARaiseReturnsWhileTheHookIsStillBlocked()
        {
            Recorder blocked = new() { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            NoticeHub hub = Hub(blocked);

            _ = hub.Raise(NoticeProbes.Started(0), Clock);
            await blocked.Entered.Task;
            for (int turn = 1; turn <= 3; turn++)
            {
                _ = hub.Raise(NoticeProbes.Started(turn), Clock);
            }

            Assert.Single(blocked.Seen);
            blocked.Hold.SetResult();
            await hub.FlushAsync("c1");
            Assert.Equal([0, 1, 2, 3], blocked.Seen.Select(notice => notice.Scope.TurnIndex ?? -1));
        }

        // Three threads raise into one conversation; Sequence order is delivery order, with no gap.
        [Fact(Timeout = 60_000)]
        public async Task SequenceOrderIsDeliveryOrderUnderThreeRaisingThreads()
        {
            const int PerThread = 20_000;
            Recorder recorder = new(timeout: null);
            NoticeHub hub = Hub(recorder);

            Thread[] raisers = [.. Enumerable.Range(0, 3).Select(_ => new Thread(RaiseMany))];
            foreach (Thread raiser in raisers)
            {
                raiser.Start();
            }

            foreach (Thread raiser in raisers)
            {
                raiser.Join();
            }

            void RaiseMany()
            {
                for (int index = 0; index < PerThread; index++)
                {
                    _ = hub.Raise(NoticeProbes.Started(index), Clock);
                }
            }

            await hub.FlushAsync("c1");
            long[] sequences = [.. recorder.Seen.Select(notice => notice.Scope.Sequence)];

            Assert.Equal(Enumerable.Range(1, 3 * PerThread).Select(value => (long)value), sequences);
            Assert.DoesNotContain(recorder.Threads, id => raisers.Any(raiser => raiser.ManagedThreadId == id));
        }

        [Fact(Timeout = 10_000)]
        public async Task ASlowHookDoesNotDelayAnotherHook()
        {
            Recorder slow = new() { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            Recorder fast = new();
            NoticeHub hub = Hub(slow, fast);

            _ = hub.Raise(NoticeProbes.Started(0), Clock);
            _ = hub.Raise(NoticeProbes.Started(1), Clock);

            await NoticeProbes.WaitUntilAsync(() => fast.Seen.Count == 2);
            await slow.Entered.Task;
            Assert.Single(slow.Seen);
            slow.Hold.SetResult();
        }

        // A hook that throws on a HookFailed fault raises no fault about it.
        [Fact(Timeout = 10_000)]
        public async Task TwoHooksThatThrowOnEverythingDoNotRaiseFaultsAboutFaults()
        {
            Recorder recorder = new();
            NoticeHub hub = Hub(new Thrower(), new Thrower(), recorder);

            _ = hub.Raise(NoticeProbes.Started(), Clock);
            await hub.FlushAsync("c1");
            await hub.FlushAsync("c1");

            Assert.Single(recorder.Seen.OfType<TurnStarted>());
            Assert.Equal(2, recorder.Seen.OfType<Fault>().Count(fault => fault.Kind == FaultKind.HookFailed));
        }

        [Fact]
        public void ANoticeNoHookWantsStillGetsAnIdentity()
        {
            NoticeHub hub = Hub(new Recorder());

            Guid eventId = hub.Raise(new StageChanged(NoticeProbes.Scope(), "a", "b"), Clock);

            Assert.NotEqual(Guid.Empty, eventId);
            Assert.Equal(0, hub.MailboxCount);
        }

        // HookNotice.EventId: a version 7 Guid per notice, and the one Raise returns is the one the hook is handed.
        [Fact(Timeout = 10_000)]
        public async Task EveryDeliveredNoticeCarriesTheVersionSevenIdItsRaiseReturned()
        {
            Recorder recorder = new();
            NoticeHub hub = Hub(recorder);

            Guid first = hub.Raise(NoticeProbes.Started(0), Clock);
            Guid second = hub.Raise(NoticeProbes.Started(1), Clock);
            await hub.FlushAsync("c1");

            Assert.Equal([first, second], recorder.Seen.Select(notice => notice.EventId));
            Assert.All(recorder.Seen, notice => Assert.Equal(7, notice.EventId.Version));
            Assert.NotEqual(first, second);
        }

        // The reader outlives the turn that started it, so it must not carry that turn's trace or async state.
        [Fact(Timeout = 10_000)]
        public async Task AHookRunsOutsideTheContextOfTheTurnThatFirstRaised()
        {
            ContextProbe probe = new();
            NoticeHub hub = Hub(probe);

            using (Activity turn = new Activity("turn").Start())
            {
                probe.Flowing.Value = "turn 0";
                _ = hub.Raise(NoticeProbes.Started(0), Clock);
                _ = hub.Raise(NoticeProbes.Started(1), Clock);
            }

            await hub.FlushAsync("c1");

            Assert.Equal([(null, null), (null, null)], probe.Seen);
        }

        [Fact(Timeout = 10_000)]
        public async Task HostNoticesHaveTheirOwnMailbox()
        {
            Recorder recorder = new();
            NoticeHub hub = Hub(recorder);

            _ = hub.Raise(new HostStarted(NoticeProbes.Scope(conversationId: null, turn: null), ["main"], 3), Clock);
            _ = hub.Raise(NoticeProbes.Started(), Clock);
            await hub.FlushAsync(conversationId: null);
            await hub.FlushAsync("c1");

            Assert.Equal(1, recorder.Seen.OfType<HostStarted>().Single().Scope.Sequence);
            Assert.Equal(1, recorder.Seen.OfType<TurnStarted>().Single().Scope.Sequence);
        }

        /// <summary>Records the ambient activity and async-local value each delivery runs under.</summary>
        private sealed class ContextProbe : AgentHook
        {
            public AsyncLocal<string?> Flowing { get; } = new();

            public ConcurrentQueue<(string? Activity, string? Flowing)> Seen { get; } = new();

            public override ValueTask OnTurnStartedAsync(TurnStarted notice, CancellationToken cancellationToken)
            {
                Seen.Enqueue((Activity.Current?.OperationName, Flowing.Value));
                return ValueTask.CompletedTask;
            }
        }
    }
}
