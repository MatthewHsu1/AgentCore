using System.Diagnostics;
using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Gates;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Hooks
{
    [Collection(GateOverheadSuite.Name)]
    [Trait("Kind", "Timing")]
    public sealed class GateOverheadTests
    {
        private const double TargetMicroseconds = 50;

        private const double AddedPerTurnMicroseconds = 3_000;

        private static readonly HookScope Scope = new("c1", "main", 0, "", Guid.CreateVersion7(), 0, DateTimeOffset.UnixEpoch);

        // The target is under 50 µs per gate with no overriding hook. A miss of the target on a
        // loaded machine is reported in the output, not failed. The bounds that fail the run are 250 µs with no
        // hook and 100 µs per no-op hook, over 40 times what this host measured.
        [Theory(Timeout = 60_000)]
        [InlineData(0, 250)]
        [InlineData(1, 100)]
        [InlineData(5, 500)]
        public async Task OneGateRunCostsMicroseconds(int hooks, double boundMicroseconds)
        {
            const int Runs = 5_000;
            GateRunner runner = new(HookTable.Build([.. NoOps(hooks)]), NullLogger.Instance, TimeProvider.System);
            _ = await RunAsync(runner);

            long started = Stopwatch.GetTimestamp();
            for (int run = 0; run < Runs; run++)
            {
                _ = await RunAsync(runner);
            }

            double mean = Stopwatch.GetElapsedTime(started).TotalMicroseconds / Runs;
            string target = mean < TargetMicroseconds ? "met" : "missed";
            TestContext.Current.TestOutputHelper?.WriteLine($"H3 gate run, {hooks} hooks: {mean:0.000} µs (50 µs target {target})");

            Assert.True(mean < boundMicroseconds, $"{hooks} hooks: {mean:0.000} µs per gate run");
        }

        // Per tool call, through the compiled pipeline: one and five no-op tool and model hooks against none.
        [Fact(Timeout = 60_000)]
        public async Task FiveHooksAddLittleToATurnWithOneToolCall()
        {
            double[] medians = await MedianTurnsAsync([], [.. NoOps(1)], [.. NoOps(5)]);
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"H3 turn with one tool call: {medians[0]:0.0} µs with no hooks, {medians[1]:0.0} µs with one, {medians[2]:0.0} µs with five");

            // One tool call and two model rounds pass 2 tool gates and 4 model gates per hook:
            // 30 gate runs for five hooks, which this host runs in tens of µs.
            Assert.True(medians[1] - medians[0] < AddedPerTurnMicroseconds, $"one hook added {medians[1] - medians[0]:0.0} µs to one turn");
            Assert.True(medians[2] - medians[0] < AddedPerTurnMicroseconds, $"five hooks added {medians[2] - medians[0]:0.0} µs to one turn");
        }

        private static ValueTask<int> RunAsync(GateRunner runner)
        {
            return runner.RunAsync(
                GatePoint.BeforeTool,
                Scope,
                0,
                _ => new ToolGate(Scope, "t", "c", new Dictionary<string, object?>(), new Dictionary<string, object?>()),
                static (hook, gate, token) => hook.BeforeToolAsync(gate, token),
                static (state, _) => state + 1,
                static state => state,
                raiseFault: null,
                CancellationToken.None);
        }

        // The median of 50 timed turns per hook list, which is steadier than a mean. The lists take turns, so a
        // slow spell of the machine lands on all of them alike, and the first round is an untimed warm-up.
        private static async Task<double[]> MedianTurnsAsync(params IReadOnlyList<AgentHook>[] hookLists)
        {
            const int Turns = 50;
            List<double>[] times = [.. hookLists.Select(_ => new List<double>())];
            for (int turn = 0; turn <= Turns; turn++)
            {
                for (int list = 0; list < hookLists.Length; list++)
                {
                    ConversationSession session = HookSessions.Create(
                        HookSessions.ToolAgentYaml, new ToolCallingChatClient("final"), hookLists[list], tools: new StubToolBuilder("{}").Create);
                    long started = Stopwatch.GetTimestamp();
                    _ = await session.RunTurnAsync("how much?", TestContext.Current.CancellationToken);
                    if (turn > 0)
                    {
                        times[list].Add(Stopwatch.GetElapsedTime(started).TotalMicroseconds);
                    }
                }
            }

            return [.. times.Select(static list => list.Order().ElementAt(list.Count / 2))];
        }

        private static IEnumerable<AgentHook> NoOps(int count)
        {
            return Enumerable.Range(0, count).Select(_ => new NoOp());
        }

        private sealed class NoOp : AgentHook
        {
            public override ValueTask BeforeToolAsync(ToolGate gate, CancellationToken cancellationToken)
            {
                return default;
            }

            public override ValueTask AfterToolAsync(ToolResultGate gate, CancellationToken cancellationToken)
            {
                return default;
            }

            public override ValueTask BeforeModelAsync(ModelGate gate, CancellationToken cancellationToken)
            {
                return default;
            }

            public override ValueTask AfterModelAsync(ModelResultGate gate, CancellationToken cancellationToken)
            {
                return default;
            }
        }
    }
}
