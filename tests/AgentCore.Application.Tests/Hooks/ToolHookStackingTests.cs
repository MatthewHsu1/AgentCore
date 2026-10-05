using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Gates;
using AgentCore.Application.Hooks.Layers;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Tests.Fakes;
using AgentCore.TestSupport;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;
using AgentCore.Application.Runtime.Turn;

namespace AgentCore.Application.Tests.Hooks
{
    /// <summary>
    /// MAF 1.21.0's function middleware wraps the run options it is handed in place, and a <c>loop:</c> entry hands
    /// every round the same options. The tool layer must still run once per call.
    /// </summary>
    public sealed class ToolHookStackingTests
    {
        private const string LoopYaml = """
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

        // An open todo keeps the loop going, so each of the two rounds makes one todos_add call (LoopCompilationTests).
        [Fact]
        public async Task EachCallOfALoopEntryIsOneNotice()
        {
            RecordingHook hook = new();
            ToolCallingChatClient model = new(
                "added",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["todos"] = new object[] { new Dictionary<string, object?>(StringComparer.Ordinal) { ["title"] = "T" } },
                },
                everyRun: true);
            ConversationSession session = HookSessions.Create(LoopYaml, model, [hook]);

            _ = await session.RunTurnAsync("fix it", Ct);
            await session.FlushNoticesAsync();

            Assert.Equal(["todos_add", "todos_add"], model.Called);
            Assert.Equal(["todos_add", "todos_add"], hook.Of<ToolCalled>().Select(called => called.ToolName));
        }

        [Fact]
        public async Task TwoRunsWithOneOptionsObjectAreOneNoticePerCall()
        {
            RecordingHook hook = new();

            int calls = await RunTwiceAsync([hook], () => "ok");

            Assert.Equal(2, calls);
            Assert.Equal(2, hook.Of<ToolCalled>().Count);
        }

        // Three attempts per call, however many runs share the options.
        [Fact]
        public async Task ARetryInTwoRunsWithOneOptionsObjectStopsAtThreeAttemptsEach()
        {
            int calls = await RunTwiceAsync([new Retrying()], string () => throw new InvalidOperationException("always"));

            Assert.Equal(6, calls);
        }

        /// <summary>Runs one gated agent twice with one options object, the way <c>LoopAgent</c> runs its rounds.</summary>
        /// <returns>How many times the tool ran.</returns>
        private static async Task<int> RunTwiceAsync(IReadOnlyList<AgentHook> hooks, Func<string> answer)
        {
            int calls = 0;
            AIFunction tool = AIFunctionFactory.Create(
                () =>
                {
                    calls++;
                    return answer();
                },
                "price_lookup");
            ToolCallingChatClient model = new("final");
            ChatClientAgent inner = new(model, new ChatClientAgentOptions { ChatOptions = new ChatOptions { Tools = [tool] } });
            await using HookRuntime runtime = HookRuntime.Create(hooks, loggers: null);
            SessionHooks session = new(runtime, "conversation", "main", TimeProvider.System);
            TurnInvocation turn = new() { ConversationId = "conversation", TurnIndex = 0, Stage = string.Empty, Hooks = session };
            AIAgent agent = ToolHookMiddleware.Apply(inner, runtime);
            AgentRunOptions options = turn.RunOptions();

            _ = await agent.RunAsync("how much?", session: null, options, Ct);
            _ = await agent.RunAsync("how much?", session: null, options, Ct);
            await session.FlushAsync();
            session.Release();

            return calls;
        }

        private sealed class Retrying : AgentHook
        {
            public override ValueTask AfterToolFailedAsync(ToolFailureGate gate, CancellationToken cancellationToken)
            {
                gate.Retry();
                return default;
            }
        }
    }
}
