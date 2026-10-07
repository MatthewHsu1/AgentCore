using System.Collections.Concurrent;
using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Gates;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Runtime;
using AgentCore.Domain;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Hooks
{
    public sealed class RunGateTests
    {
        private const string TodosLoopYaml = """
        apiVersion: agentcore/v1
        agents:
          defaults: { clock: false }
          items:
            - { id: coder, instructions: "fix bugs", todos: true, loop: { maxRounds: 2, until: [{ todos: {} }] } }
        entries:
          main:
            agent: coder
        """;

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // Instructions a run gate adds are seen on every round of that run.
        [Fact]
        public async Task InstructionsAddedByAHookReachEveryRoundOfTheRun()
        {
            RequestsSeen model = new(new ToolCallingChatClient("final"));
            Gate hook = new(gate => gate.AddInstructions("Speak like a pirate."));
            ConversationSession session = HookSessions.Create(HookSessions.ToolAgentYaml, model, [hook], tools: new StubToolBuilder("{}").Create);

            _ = await session.RunTurnAsync("how much?", Ct);

            Assert.Equal(2, model.Requests.Count);
            Assert.All(model.Requests, request => Assert.Contains("Speak like a pirate.", request.Instructions, StringComparison.Ordinal));
        }

        // The turn's own run is not nested; an agent-as-tool child is.
        [Fact]
        public async Task TheTurnsRunIsNotNestedAndAnAgentToolChildIs()
        {
            List<(string AgentId, bool Nested)> seen = [];
            Gate hook = new(gate => seen.Add((gate.AgentId, gate.Nested)));
            ToolCallingChatClient model = new("done", new Dictionary<string, object?>(StringComparer.Ordinal) { ["query"] = "help me" });
            ConversationSession session = HookSessions.Create(HookSessions.DelegatingYaml, model, [hook]);

            _ = await session.RunTurnAsync("go", Ct);

            Assert.Equal([("only", false), ("helper", true)], seen);
        }

        // A graph participant is a run of its own, so the gate fires for each one, nested, under the turn
        // that runs the graph. Two turns: a graph row that keeps its workflow session must not replay the first.
        // A concurrent row runs its participants at once, so the order within a turn is not asserted.
        [Theory]
        [InlineData(HookSessions.GraphYaml)]
        [InlineData(HookSessions.KeptGraphYaml)]
        [InlineData(HookSessions.ExplicitGraphYaml)]
        [InlineData(HookSessions.ConcurrentGraphYaml)]
        public async Task EachParticipantOfAGraphRowFiresTheGateNested(string yaml)
        {
            ConcurrentQueue<(string AgentId, bool Nested, int? Turn)> seen = [];
            Gate hook = new(gate => seen.Enqueue((gate.AgentId, gate.Nested, gate.Scope.TurnIndex)));
            ConversationSession session = HookSessions.Create(yaml, new ToolCallingChatClient("done"), [hook], tools: new StubToolBuilder("{}").Create);

            _ = await session.RunTurnAsync("first", Ct);
            _ = await session.RunTurnAsync("second", Ct);

            Assert.Equal(
                [("researcher", true, 0), ("responder", true, 0), ("researcher", true, 1), ("responder", true, 1)],
                seen.OrderBy(static fired => fired.Turn).ThenBy(static fired => fired.AgentId, StringComparer.Ordinal));
        }

        [Fact]
        public async Task ATurnNoteAndARunNoteEachArriveOncePerRound()
        {
            RequestsSeen model = new(new ToolCallingChatClient("final"));
            TurnNote turnHook = new();
            Gate runHook = new(gate => gate.AddMessages([new ChatMessage(ChatRole.System, "per-run note")]));
            ConversationSession session = HookSessions.Create(HookSessions.ToolAgentYaml, model, [turnHook, runHook], tools: new StubToolBuilder("{}").Create);

            _ = await session.RunTurnAsync("how much?", Ct);

            Assert.Equal(2, model.Requests.Count);
            Assert.All(model.Requests, request =>
            {
                Assert.Equal(1, request.Messages.Count(text => text == "per-turn note"));
                Assert.Equal(1, request.Messages.Count(text => text == "per-run note"));
            });
        }

        // Every pass of a loop is a run: the gate fires again and the tool it adds is offered once, never stacked.
        [Fact]
        public async Task AToolAddedByAHookIsOfferedOncePerRoundAcrossALoopsIterations()
        {
            RequestsSeen model = new(new ToolCallingChatClient(
                "added",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["todos"] = new object[] { new Dictionary<string, object?>(StringComparer.Ordinal) { ["title"] = "T" } },
                }));
            int fired = 0;
            Gate hook = new(gate =>
            {
                _ = Interlocked.Increment(ref fired);
                gate.AddTools([AIFunctionFactory.Create(() => "extra answer", "extra_tool")]);
            });
            ConversationSession session = HookSessions.Create(TodosLoopYaml, model, [hook]);

            _ = await session.RunTurnAsync("fix it", Ct);

            Assert.Equal(2, fired);
            Assert.All(model.Requests, request => Assert.Equal(1, request.Tools.Count(name => name == "extra_tool")));
        }

        // Fail-open : a hook that stages and then throws adds nothing; the next hook still does.
        [Fact]
        public async Task AHookThatThrowsAfterStagingAddsNothingAndTheNextHookStillDoes()
        {
            RequestsSeen model = new(new ScriptedChatClient("done"));
            Gate failing = new(StageAllThenThrow);
            Gate healthy = new(gate => StageAll(gate, "healthy"));
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, model, [failing, healthy]);

            _ = await session.RunTurnAsync("hi", Ct);

            RequestsSeen.Request request = Assert.Single(model.Requests);
            Assert.DoesNotContain("failing", request.Instructions, StringComparison.Ordinal);
            Assert.DoesNotContain("failing note", request.Messages);
            Assert.DoesNotContain("failing_tool", request.Tools);
            Assert.Contains("healthy", request.Instructions, StringComparison.Ordinal);
            Assert.Contains("healthy note", request.Messages);
            Assert.Contains("healthy_tool", request.Tools);
        }

        // The deadline is 2 s. A hook still running past it is abandoned and its staged additions dropped.
        [Fact(Timeout = 10_000)]
        public async Task AHookThatMissesTheDeadlineAddsNothingAndTheNextHookStillDoes()
        {
            FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
            RequestsSeen model = new(new ScriptedChatClient("done"));
            TaskCompletionSource never = new(TaskCreationOptions.RunContinuationsAsynchronously);
            SlowHook slow = new(never.Task);
            Gate healthy = new(gate => StageAll(gate, "healthy"));
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, model, [slow, healthy], time: time);

            Task<TurnResult> turn = session.RunTurnAsync("hi", Ct);
            await slow.Started.Task.WaitAsync(Ct);
            await time.WaitForTimersAsync(DateTimeOffset.UnixEpoch + GatePoint.BeforeRun.Deadline, 2);
            time.Advance(GatePoint.BeforeRun.Deadline);
            _ = await turn;
            never.SetResult();

            RequestsSeen.Request request = Assert.Single(model.Requests);
            Assert.DoesNotContain("slow", request.Instructions, StringComparison.Ordinal);
            Assert.Contains("healthy", request.Instructions, StringComparison.Ordinal);
            Assert.Contains("healthy_tool", request.Tools);
        }

        private static void StageAll(RunGate gate, string name)
        {
            gate.AddInstructions(name);
            gate.AddMessages([new ChatMessage(ChatRole.System, $"{name} note")]);
            gate.AddTools([AIFunctionFactory.Create(() => "answer", $"{name}_tool")]);
        }

        private static void StageAllThenThrow(RunGate gate)
        {
            StageAll(gate, "failing");
            throw new InvalidOperationException("hook failed");
        }

        private sealed class SlowHook(Task hang) : AgentHook
        {
            public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public override async ValueTask BeforeRunAsync(RunGate gate, CancellationToken cancellationToken)
            {
                StageAll(gate, "slow");
                Started.SetResult();
                await hang;
            }
        }

        private sealed class Gate(Action<RunGate> decide) : AgentHook
        {
            public override ValueTask BeforeRunAsync(RunGate gate, CancellationToken cancellationToken)
            {
                decide(gate);
                return default;
            }
        }

        private sealed class TurnNote : AgentHook
        {
            public override ValueTask BeforeTurnAsync(TurnGate gate, CancellationToken cancellationToken)
            {
                gate.AddContext("per-turn note");
                return default;
            }
        }
    }
}
