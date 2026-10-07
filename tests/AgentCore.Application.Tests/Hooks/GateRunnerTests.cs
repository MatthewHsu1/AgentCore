using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Gates;
using AgentCore.Application.Hooks.Notices;
using AgentCore.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentCore.Application.Tests.Hooks
{
    public sealed class GateRunnerTests
    {
        private static readonly HookScope Scope = new("c1", "main", 0, "", Guid.CreateVersion7(), 0, DateTimeOffset.UnixEpoch);

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private sealed record Decision(bool Blocked, string Sku, int Seen);

        [Fact]
        public async Task AModifyingVerbIsWhatTheNextHookSees()
        {
            List<string> seen = [];
            Hook first = new((gate, _) => { gate.ReplaceArguments(Args("B2")); return default; });
            Hook second = new((gate, _) => { seen.Add((string)gate.Arguments["sku"]!); return default; });

            Decision result = await RunAsync([first, second], new FakeTimeProvider(DateTimeOffset.UnixEpoch), faults: null);

            Assert.Equal(["B2"], seen);
            Assert.Equal("B2", result.Sku);
        }

        [Fact]
        public async Task ATerminalVerbEndsTheChain()
        {
            Hook first = new((gate, _) => { gate.Block("no"); return default; });
            Hook second = new((_, _) => throw new InvalidOperationException("must not run"));

            Decision result = await RunAsync([first, second], new FakeTimeProvider(DateTimeOffset.UnixEpoch), faults: null);

            Assert.True(result.Blocked);
            Assert.Equal(0, second.Calls);
        }

        // A hook that ignores its token is abandoned at the deadline; its view is sealed.
        [Fact(Timeout = 10_000)]
        public async Task AHookThatNeverReturnsIsAbandonedAtTheDeadlineAndItsLateVerbThrows()
        {
            FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<Exception?> late = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Hook stuck = new(async (gate, _) =>
            {
                await release.Task;
                try { gate.Block("too late"); late.SetResult(null); }
                catch (InvalidOperationException exception) { late.SetResult(exception); }
            });
            Hook next = new((_, _) => default);
            List<(Fault Fault, AgentHook Except)> faults = [];

            Task<Decision> running = RunAsync([stuck, next], time, faults).AsTask();
            await time.WaitForTimersAsync(2);
            time.Advance(GatePoint.BeforeTool.Deadline);
            Decision result = await running;
            release.SetResult();

            Assert.False(result.Blocked);
            Assert.Equal(1, next.Calls);
            _ = Assert.IsType<InvalidOperationException>(await late.Task);
            (Fault fault, AgentHook except) = Assert.Single(faults);
            Assert.Equal(FaultKind.HookFailed, fault.Kind);
            Assert.Same(stuck, except);
        }

        // A hook that blocks its thread before its first await is abandoned at the deadline too.
        [Fact(Timeout = 10_000)]
        public async Task AHookThatBlocksItsThreadIsAbandonedAtTheDeadline()
        {
            FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
            object gate = new();
            bool released = false;
            Hook blocking = new((_, _) =>
            {
                lock (gate)
                {
                    while (!released)
                    {
                        _ = Monitor.Wait(gate);
                    }
                }

                return default;
            });
            Hook next = new((_, _) => default);
            List<(Fault Fault, AgentHook Except)> faults = [];

            try
            {
                Task<Decision> running = Task.Run(() => RunAsync([blocking, next], time, faults).AsTask(), Ct);
                await time.WaitForTimersAsync(2).WaitAsync(TimeSpan.FromSeconds(5), Ct);
                time.Advance(GatePoint.BeforeTool.Deadline);
                _ = await running.WaitAsync(TimeSpan.FromSeconds(5), Ct);

                Assert.Equal(1, next.Calls);
                Assert.Same(blocking, Assert.Single(faults).Except);
            }
            finally
            {
                lock (gate)
                {
                    released = true;
                    Monitor.PulseAll(gate);
                }
            }
        }

        [Fact(Timeout = 10_000)]
        public async Task AHookThatHonoursItsTokenSeesItCancelledAtTheDeadline()
        {
            FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
            TaskCompletionSource<bool> cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Hook polite = new(async (_, token) =>
            {
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { cancelled.SetResult(true); throw; }
            });

            using RecordingLoggerFactory logs = new();
            GateRunner runner = new(HookTable.Build([polite]), logs.CreateLogger("hooks"), time);

            Task<Decision> running = Run(runner, faults: null, Ct).AsTask();
            await time.WaitForTimersAsync(2);
            time.Advance(GatePoint.BeforeTool.Deadline);
            _ = await running;

            Assert.True(await cancelled.Task);
            _ = Assert.Single(logs.Of(201));
            Assert.Empty(logs.Of(200));
        }

        // Open drops the failing hook's staged verbs and goes on.
        [Fact]
        public async Task AThrowingHookFailsOpenByDefault()
        {
            Hook thrower = new((gate, _) => { gate.ReplaceArguments(Args("X9")); throw new InvalidOperationException("boom"); });
            Hook next = new((_, _) => default);
            List<(Fault Fault, AgentHook Except)> faults = [];

            Decision result = await RunAsync([thrower, next], new FakeTimeProvider(DateTimeOffset.UnixEpoch), faults);

            Assert.Equal("A1", result.Sku);
            Assert.Equal(1, next.Calls);
            _ = Assert.Single(faults);
        }

        // A hook may fail closed per point; the safe verb applies and the chain stops.
        [Fact]
        public async Task AHookThatFailsClosedAppliesTheSafeVerbAndStops()
        {
            Hook thrower = new((_, _) => throw new InvalidOperationException("boom")) { Failure = HookFailure.Closed };
            Hook next = new((_, _) => default);

            Decision result = await RunAsync([thrower, next], new FakeTimeProvider(DateTimeOffset.UnixEpoch), faults: null);

            Assert.True(result.Blocked);
            Assert.Equal(0, next.Calls);
        }

        // The hook's token carries the turn's cancellation, even after the runner stopped waiting.
        [Fact(Timeout = 10_000)]
        public async Task TheTurnsOwnCancellationIsNotAHookFailure()
        {
            using CancellationTokenSource turn = new();
            TaskCompletionSource hookCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Hook waiting = new(async (_, token) =>
            {
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { hookCancelled.SetResult(); throw; }
            });
            List<(Fault Fault, AgentHook Except)> faults = [];
            GateRunner runner = new(HookTable.Build([waiting]), NullLogger.Instance, new FakeTimeProvider(DateTimeOffset.UnixEpoch));

            ValueTask<Decision> running = Run(runner, faults, turn.Token);
            await turn.CancelAsync();

            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await running);
            await hookCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            Assert.Empty(faults);
        }

        // A hook's own TimeoutException, thrown in time, is a failure with its stack, not a missed deadline.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AHookThatThrowsItsOwnTimeoutInTimeIsLoggedAsAFailure(bool afterAnAwait)
        {
            TimeoutException thrown = new("the hook's own backend timed out");
            Hook thrower = new(afterAnAwait
                ? async (_, _) => { await Task.Yield(); throw thrown; }
            : (_, _) => throw thrown);
            using RecordingLoggerFactory logs = new();
            GateRunner runner = new(
                HookTable.Build([thrower]), logs.CreateLogger("hooks"), new FakeTimeProvider(DateTimeOffset.UnixEpoch));

            _ = await Run(runner, faults: null, Ct);

            Assert.Same(thrown, Assert.Single(logs.Of(200)).Exception);
            Assert.Empty(logs.Of(201));
        }

        private static ValueTask<Decision> RunAsync(Hook[] hooks, FakeTimeProvider time, List<(Fault, AgentHook)>? faults)
        {
            return Run(new GateRunner(HookTable.Build(hooks), NullLogger.Instance, time), faults, Ct);
        }

        private static ValueTask<Decision> Run(GateRunner runner, List<(Fault, AgentHook)>? faults, CancellationToken cancellationToken)
        {
            return runner.RunAsync(
                GatePoint.BeforeTool,
                Scope,
                new Decision(false, "A1", 0),
                state => new ToolGate(Scope, "lookup", "call-1", Args(state.Sku), new Dictionary<string, object?>()),
                (hook, gate, token) => hook.BeforeToolAsync(gate, token),
                (state, gate) => state with
                {
                    Blocked = state.Blocked || gate.Blocked,
                    Sku = gate.NewArguments is { } args ? (string)args["sku"]! : state.Sku,
                },
                state => state with { Blocked = true },
                faults is null ? null : (fault, except) => { lock (faults) { faults.Add((fault, except)); } },
                cancellationToken);
        }

        private static Dictionary<string, object?> Args(string sku)
        {
            return new(StringComparer.Ordinal) { ["sku"] = sku };
        }

        private sealed class Hook(Func<ToolGate, CancellationToken, ValueTask> body) : AgentHook
        {
            private int _calls;

            public int Calls => Volatile.Read(ref _calls);

            public HookFailure? Failure { get; init; }

            public override ValueTask BeforeToolAsync(ToolGate gate, CancellationToken cancellationToken)
            {
                _ = Interlocked.Increment(ref _calls);
                return body(gate, cancellationToken);
            }

            public override HookFailure FailureFor(GatePoint point)
            {
                return Failure ?? base.FailureFor(point);
            }
        }
    }
}
