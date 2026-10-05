using System.Collections.Concurrent;
using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Domain.Audit;
using AgentCore.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentCore.Application.Tests.Hooks
{
    public sealed class NoticeHubLifetimeTests
    {
        private static readonly FakeTimeProvider Clock = new(DateTimeOffset.UnixEpoch);

        // After ConversationEnded, only notices of a turn admitted before the end get through.
        // The unload and a refusal are lifecycle facts and still do ("unload and end drop nothing").
        [Fact(Timeout = 10_000)]
        public async Task AfterTheEndOnlyTurnsAdmittedBeforeItGetThrough()
        {
            Guid session = Guid.CreateVersion7();
            Recorder recorder = new();
            NoticeHub hub = new(HookTable.Build([recorder]), NullLogger.Instance, TimeProvider.System);
            FakeTimeProvider clock = new(DateTimeOffset.UnixEpoch);

            _ = hub.Raise(new ConversationEnded(NoticeProbes.Scope(turn: null, session: session), ConversationEndReason.CallerHungUp, null, null), clock, endsAfterTurn: 2);
            _ = hub.Raise(NoticeProbes.Started(2, session), clock);
            _ = hub.Raise(NoticeProbes.Started(3, session), clock);
            _ = hub.Raise(new ConversationUnloaded(NoticeProbes.Scope(turn: null, session: session), UnloadCause.Closed), clock);
            _ = hub.Raise(new TurnRefused(NoticeProbes.Scope(turn: null, session: session), TurnRefusal.Terminal, AfterEnd: true), clock);
            _ = hub.Raise(NoticeProbes.Started(3, Guid.CreateVersion7()), clock);
            await hub.FlushAsync("c1");

            Assert.Equal(
                ["ConversationEnded:", "TurnStarted:2", "ConversationUnloaded:", "TurnRefused:", "TurnStarted:3"],
                recorder.Seen.Select(notice => $"{notice.GetType().Name}:{notice.Scope.TurnIndex}"));
            Assert.NotEqual(session, recorder.Seen[^1].Scope.SessionId);
        }

        // A hook that fails on ConversationEnded raises its Fault after the end; the other hooks must still hear it.
        [Fact(Timeout = 10_000)]
        public async Task AFaultRaisedAfterTheEndStillReachesTheOtherHooks()
        {
            Guid session = Guid.CreateVersion7();
            Recorder recorder = new();
            NoticeHub hub = new(HookTable.Build([new Thrower(), recorder]), NullLogger.Instance, TimeProvider.System);
            FakeTimeProvider clock = new(DateTimeOffset.UnixEpoch);

            _ = hub.Raise(new ConversationEnded(NoticeProbes.Scope(turn: null, session: session), ConversationEndReason.CallerHungUp, null, null), clock, endsAfterTurn: -1);
            await hub.FlushAsync("c1");
            await hub.FlushAsync("c1");

            Assert.Equal([nameof(ConversationEnded), nameof(Fault)], recorder.Seen.Select(notice => notice.GetType().Name));
            Assert.Equal(FaultKind.HookFailed, recorder.Seen.OfType<Fault>().Single().Kind);
        }

        // A Fault inside a turn follows its turn's admission; only a Fault about the conversation is exempt.
        [Fact(Timeout = 10_000)]
        public async Task AFaultAfterTheEndGetsThroughOnlyForTheConversationOrAnAdmittedTurn()
        {
            Guid session = Guid.CreateVersion7();
            Recorder recorder = new();
            NoticeHub hub = new(HookTable.Build([recorder]), NullLogger.Instance, TimeProvider.System);

            _ = hub.Raise(new ConversationEnded(NoticeProbes.Scope(turn: null, session: session), ConversationEndReason.CallerHungUp, null, null), Clock, endsAfterTurn: 2);
            _ = hub.Raise(new Fault(NoticeProbes.Scope(turn: 3, session: session), FaultKind.HookFailed, "late turn", null), Clock);
            _ = hub.Raise(new Fault(NoticeProbes.Scope(turn: null, session: session), FaultKind.HookFailed, "conversation", null), Clock);
            _ = hub.Raise(new Fault(NoticeProbes.Scope(turn: 2, session: session), FaultKind.HookFailed, "admitted turn", null), Clock);
            await hub.FlushAsync("c1");

            Assert.Equal(["conversation", "admitted turn"], recorder.Seen.OfType<Fault>().Select(fault => fault.Message));
        }

        // An end no hook hears still closes the conversation to later turns, and makes no mailbox of its own.
        [Fact(Timeout = 10_000)]
        public async Task AnEndNoHookWantsStillHoldsBackLaterTurnsWithoutMakingAMailbox()
        {
            Guid session = Guid.CreateVersion7();
            TurnsOnly hook = new();
            NoticeHub hub = new(HookTable.Build([hook]), NullLogger.Instance, TimeProvider.System);
            FakeTimeProvider clock = new(DateTimeOffset.UnixEpoch);

            _ = hub.Raise(new ConversationEnded(NoticeProbes.Scope("c2", turn: null, session), ConversationEndReason.CallerHungUp, null, null), clock, endsAfterTurn: -1);
            Assert.Equal(0, hub.MailboxCount);

            _ = hub.Raise(NoticeProbes.Started(0, session), clock);
            _ = hub.Raise(new ConversationEnded(NoticeProbes.Scope(turn: null, session: session), ConversationEndReason.CallerHungUp, null, null), clock, endsAfterTurn: 0);
            _ = hub.Raise(NoticeProbes.Started(1, session), clock);
            await hub.FlushAsync("c1");

            Assert.Equal([0], hook.Turns);
            Assert.Equal(1, hub.MailboxCount);
        }

        // An idle reader removes its mailbox; a later raise makes a new one and the counter restarts.
        [Fact(Timeout = 10_000)]
        public async Task AnIdleMailboxIsEvictedAndTheNextRaiseStartsANewOne()
        {
            FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
            Recorder recorder = new();
            NoticeHub hub = new(HookTable.Build([recorder]), NullLogger.Instance, time);

            _ = hub.Raise(NoticeProbes.Started(0), time);
            _ = hub.Raise(NoticeProbes.Started(1), time);
            await hub.FlushAsync("c1");
            await time.WaitForTimersAsync(time.GetUtcNow() + NoticeHub.IdleAfter, 1);
            time.Advance(NoticeHub.IdleAfter);
            await NoticeProbes.WaitUntilAsync(() => hub.MailboxCount == 0);

            _ = hub.Raise(NoticeProbes.Started(2), time);
            await hub.FlushAsync("c1");

            Assert.Equal([1L, 2L, 1L], recorder.Seen.Select(notice => notice.Scope.Sequence));
        }

        // A loaded session pins its mailbox, so idling does not restart its Sequence.
        [Fact(Timeout = 10_000)]
        public async Task APinnedMailboxOutlivesItsIdleTimeAndKeepsCounting()
        {
            FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
            Recorder recorder = new();
            NoticeHub hub = new(HookTable.Build([recorder]), NullLogger.Instance, time);

            hub.Pin("c1");
            _ = hub.Raise(NoticeProbes.Started(0), time);
            _ = hub.Raise(NoticeProbes.Started(1), time);
            await hub.FlushAsync("c1");
            await time.WaitForTimersAsync(time.GetUtcNow() + NoticeHub.IdleAfter, 1);
            time.Advance(NoticeHub.IdleAfter);
            // The refused eviction shows as the reader arming its next idle wait.
            await time.WaitForTimersAsync(time.GetUtcNow() + NoticeHub.IdleAfter, 1);

            _ = hub.Raise(NoticeProbes.Started(2), time);
            await hub.FlushAsync("c1");

            Assert.Equal(1, hub.MailboxCount);
            Assert.Equal([1L, 2L, 3L], recorder.Seen.Select(notice => notice.Scope.Sequence));
        }

        [Fact(Timeout = 10_000)]
        public async Task AReleasedMailboxIsEvictedOnceItsReaderIdles()
        {
            FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
            NoticeHub hub = new(HookTable.Build([new Recorder()]), NullLogger.Instance, time);

            hub.Pin("c1");
            hub.Pin("c1");
            _ = hub.Raise(NoticeProbes.Started(0), time);
            await hub.FlushAsync("c1");
            hub.Release("c1");
            hub.Release("c1");
            await time.WaitForTimersAsync(time.GetUtcNow() + NoticeHub.IdleAfter, 1);
            time.Advance(NoticeHub.IdleAfter);

            await NoticeProbes.WaitUntilAsync(() => hub.MailboxCount == 0);
        }

        // A mailbox that never started a reader has no idle timer to evict it, so the last release does.
        [Fact]
        public void AMailboxPinnedButNeverWrittenGoesAtTheLastRelease()
        {
            NoticeHub hub = new(HookTable.Build([new Recorder()]), NullLogger.Instance, TimeProvider.System);

            hub.Pin("c1");
            hub.Pin("c1");
            hub.Release("c1");
            Assert.Equal(1, hub.MailboxCount);

            hub.Release("c1");
            Assert.Equal(0, hub.MailboxCount);
        }

        // Every channel is completed and drained; nothing raised after the stop is delivered.
        [Fact(Timeout = 10_000)]
        public async Task StopDrainsWhatWasRaisedAndRefusesWhatComesAfter()
        {
            Recorder recorder = new() { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            NoticeHub hub = new(HookTable.Build([recorder]), NullLogger.Instance, TimeProvider.System);
            FakeTimeProvider clock = new(DateTimeOffset.UnixEpoch);

            _ = hub.Raise(NoticeProbes.Started(0), clock);
            _ = hub.Raise(NoticeProbes.Started(1), clock);
            ValueTask stopping = hub.StopAsync(TimeSpan.FromSeconds(5));
            recorder.Hold.SetResult();
            await stopping;
            _ = hub.Raise(NoticeProbes.Started(2), clock);

            Assert.Equal([0, 1], recorder.Seen.Select(notice => notice.Scope.TurnIndex ?? -1));
        }

        // A host shutdown timeout too long for a timer means no limit; a negative one means do not wait.
        [Fact(Timeout = 10_000)]
        public async Task AStopLongerThanATimerCanWaitStillDrains()
        {
            Recorder recorder = new() { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            NoticeHub hub = new(HookTable.Build([recorder]), NullLogger.Instance, TimeProvider.System);

            _ = hub.Raise(NoticeProbes.Started(0), Clock);
            _ = hub.Raise(NoticeProbes.Started(1), Clock);
            ValueTask stopping = hub.StopAsync(TimeSpan.MaxValue);
            recorder.Hold.SetResult();
            await stopping;

            Assert.Equal([0, 1], recorder.Seen.Select(notice => notice.Scope.TurnIndex ?? -1));
        }

        [Fact(Timeout = 10_000)]
        public async Task ANegativeStopTimeoutGivesUpAtOnce()
        {
            RecordingLoggerFactory logs = new();
            Recorder recorder = new() { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            NoticeHub hub = new(HookTable.Build([recorder]), logs.CreateLogger("hooks"), TimeProvider.System);

            _ = hub.Raise(NoticeProbes.Started(0), Clock);
            await recorder.Entered.Task;
            await hub.StopAsync(TimeSpan.FromSeconds(-5));
            recorder.Hold.SetResult();

            Assert.Equal(1, Assert.Single(logs.Of(205)).Field<int>("Mailboxes"));
        }

        [Fact]
        public void AConversationIsReloadedWhenItsMailboxHoldsAnEarlierSession()
        {
            NoticeHub hub = new(HookTable.Build([new Recorder()]), NullLogger.Instance, TimeProvider.System);
            Guid first = Guid.CreateVersion7();
            Guid second = Guid.CreateVersion7();

            _ = hub.Raise(NoticeProbes.Started(0, first), new FakeTimeProvider(DateTimeOffset.UnixEpoch));

            Assert.False(hub.LoadedBefore("c1", first));
            Assert.True(hub.LoadedBefore("c1", second));
            Assert.False(hub.LoadedBefore("c2", second));
        }

        /// <summary>Hears turn starts only, so no hook wants <see cref="ConversationEnded"/>.</summary>
        private sealed class TurnsOnly : AgentHook
        {
            public ConcurrentQueue<int> Turns { get; } = new();

            public override ValueTask OnTurnStartedAsync(TurnStarted notice, CancellationToken cancellationToken)
            {
                Turns.Enqueue(notice.Scope.TurnIndex ?? -1);
                return ValueTask.CompletedTask;
            }
        }
    }
}
