using System.Collections.Concurrent;
using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Notices;
using AgentCore.TestSupport;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentCore.Application.Tests.Hooks
{
    public sealed class NoticeHubTimeoutTests
    {
        // With a timeout set, the delivery is abandoned, logged once with hook and notice type,
        // and Fault(HookFailed) goes to the other hooks only.
        [Fact(Timeout = 10_000)]
        public async Task ATimedOutDeliveryIsAbandonedAndTheOtherHooksHearOfIt()
        {
            FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
            RecordingLoggerFactory logs = new();
            Recorder stuck = new(timeout: TimeSpan.FromSeconds(1)) { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            Recorder other = new();
            NoticeHub hub = new(HookTable.Build([stuck, other]), logs.CreateLogger("hooks"), time);

            _ = hub.Raise(NoticeProbes.Started(0), time);
            await stuck.Entered.Task;
            await time.WaitForTimersAsync(time.GetUtcNow() + TimeSpan.FromSeconds(1), 2);
            time.Advance(TimeSpan.FromSeconds(1));
            _ = hub.Raise(NoticeProbes.Started(1), time);
            await NoticeProbes.WaitUntilAsync(() => stuck.Seen.Count == 2);
            stuck.Hold.SetResult();
            await hub.FlushAsync("c1");

            Fault fault = Assert.Single(other.Seen.OfType<Fault>());
            Assert.Equal(FaultKind.HookFailed, fault.Kind);
            Assert.DoesNotContain(stuck.Seen, notice => notice is Fault);
            CapturedLine line = Assert.Single(logs.Of(203));
            Assert.EndsWith(nameof(Recorder), line.Field<string>("HookType"), StringComparison.Ordinal);
            Assert.Equal(nameof(TurnStarted), line.Field<string>("NoticeType"));
        }

        // With null, AgentCore keeps waiting, logs every 30 s, raises the fault once, and drops nothing.
        [Fact(Timeout = 10_000)]
        public async Task ANullTimeoutWaitsLogsEveryThirtySecondsAndDropsNothing()
        {
            FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
            RecordingLoggerFactory logs = new();
            Recorder patient = new(timeout: null) { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            Recorder other = new();
            NoticeHub hub = new(HookTable.Build([patient, other]), logs.CreateLogger("hooks"), time);

            _ = hub.Raise(NoticeProbes.Started(0), time);
            _ = hub.Raise(NoticeProbes.Started(1), time);
            await patient.Entered.Task;
            for (int wait = 1; wait <= 2; wait++)
            {
                await time.WaitForTimersAsync(time.GetUtcNow() + NoticeHub.StillWaitingEvery, 1);
                time.Advance(NoticeHub.StillWaitingEvery);
            }

            // The still-waiting line is written on the timer's continuation, which released work could outrun.
            await NoticeProbes.WaitUntilAsync(() => logs.Of(204).Count == 2);
            patient.Hold.SetResult();
            await hub.FlushAsync("c1");

            Assert.Equal([0, 1], patient.Seen.OfType<TurnStarted>().Select(notice => notice.Scope.TurnIndex ?? -1));
            Assert.Equal(2, logs.Of(204).Count);
            _ = Assert.Single(other.Seen.OfType<Fault>());
        }

        public static TheoryData<TimeSpan> LongerThanATimerCanWait => [TimeSpan.MaxValue, TimeSpan.FromDays(60), Timeout.InfiniteTimeSpan];

        // A timer refuses these, so they mean what null means: keep waiting, log every 30 s, drop nothing.
        [Theory(Timeout = 10_000)]
        [MemberData(nameof(LongerThanATimerCanWait))]
        public async Task ATimeoutLongerThanATimerCanWaitMeansKeepWaiting(TimeSpan timeout)
        {
            FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
            RecordingLoggerFactory logs = new();
            Recorder patient = new(timeout) { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            NoticeHub hub = new(HookTable.Build([patient]), logs.CreateLogger("hooks"), time);

            _ = hub.Raise(NoticeProbes.Started(0), time);
            _ = hub.Raise(NoticeProbes.Started(1), time);
            await patient.Entered.Task;
            await time.WaitForTimersAsync(time.GetUtcNow() + NoticeHub.StillWaitingEvery, 1);
            time.Advance(NoticeHub.StillWaitingEvery);

            // The still-waiting line is written on the timer's continuation, which released work could outrun.
            await NoticeProbes.WaitUntilAsync(() => logs.Of(204).Count == 1);
            patient.Hold.SetResult();
            await hub.FlushAsync("c1");

            Assert.Equal([0, 1], patient.Seen.Select(notice => notice.Scope.TurnIndex ?? -1));
            _ = Assert.Single(logs.Of(204));
            Assert.Empty(logs.Of(203));
        }

        // AgentHook.NoticeTimeout's default is 30 s; a negative timeout is a mistake and gets it.
        [Fact(Timeout = 10_000)]
        public async Task ANegativeTimeoutFallsBackToThirtySeconds()
        {
            FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
            RecordingLoggerFactory logs = new();
            Recorder stuck = new(TimeSpan.FromSeconds(-5)) { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            NoticeHub hub = new(HookTable.Build([stuck]), logs.CreateLogger("hooks"), time);

            _ = hub.Raise(NoticeProbes.Started(0), time);
            await stuck.Entered.Task;
            await time.WaitForTimersAsync(time.GetUtcNow() + TimeSpan.FromSeconds(30), 2);
            time.Advance(TimeSpan.FromSeconds(30));
            await hub.FlushAsync("c1");
            stuck.Hold.SetResult();

            Assert.Equal(30_000L, Assert.Single(logs.Of(203)).Field<long>("TimeoutMs"));
        }

        // An engine failure (here, the log sink throwing) must not end the hook's reader: its later notices still go.
        [Fact(Timeout = 10_000)]
        public async Task AReaderOutlivesAFailureOfTheEngineItself()
        {
            BrokenSink sink = new();
            NoticeHub hub = new(HookTable.Build([new Thrower()]), sink, TimeProvider.System);

            _ = hub.Raise(NoticeProbes.Started(0), TimeProvider.System);
            _ = hub.Raise(NoticeProbes.Started(1), TimeProvider.System);
            await hub.FlushAsync("c1");

            Assert.Equal([206, 206], sink.Written);
        }

        /// <summary>A logger that throws on the "hook failed" line, the way a broken sink would.</summary>
        private sealed class BrokenSink : ILogger
        {
            public ConcurrentQueue<int> Written { get; } = new();

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (eventId.Id == 202)
                {
                    throw new InvalidOperationException("the log sink is down");
                }

                Written.Enqueue(eventId.Id);
            }
        }
    }
}
