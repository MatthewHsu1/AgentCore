using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Gates;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Runtime;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;
using AgentCore.Application.Runtime.ToolCalls;
using AgentCore.Application.Runtime.Turn;

namespace AgentCore.Application.Tests.Hooks
{
    public sealed class ToolGateTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // The tool does not run; the model reads the block as the tool's answer.
        [Fact]
        public async Task ABlockedToolNeverRunsAndTheModelReadsTheBlock()
        {
            StubToolBuilder stub = new("""{ "price": 50 }""");
            ToolCallingChatClient model = new("final");
            Gate hook = new(before: gate => gate.Block("not allowed here"));
            RecordingHook notices = new();
            ConversationSession session = HookSessions.Create(HookSessions.ToolAgentYaml, model, [hook, notices], tools: stub.Create);

            _ = await session.RunTurnAsync("how much?", Ct);
            await session.FlushNoticesAsync();

            Assert.Empty(stub.Called);
            Assert.Contains(model.ToolResults, result => result.Contains("not allowed here", StringComparison.Ordinal));
            Assert.Equal(ToolOutcome.Blocked, Assert.Single(notices.Of<ToolCalled>()).Outcome);
        }

        // Replaced arguments reach the tool, and the turn stays filed beside them.
        [Fact]
        public async Task ReplacedArgumentsReachTheToolAndKeepTheTurnFiled()
        {
            List<(string Sku, bool Filed)> seen = [];
            ToolCallingChatClient model = new("final", new Dictionary<string, object?>(StringComparer.Ordinal) { ["sku"] = "A1" });
            Gate hook = new(before: gate => gate.ReplaceArguments(new Dictionary<string, object?>(StringComparer.Ordinal) { ["sku"] = "B2" }));
            ConversationSession session = HookSessions.Create(
                HookSessions.ToolAgentYaml,
                model,
                [hook],
                tools: tool => AIFunctionFactory.Create(
                    (string sku, AIFunctionArguments arguments) =>
                    {
                        seen.Add((sku, TurnInvocation.FiledIn(arguments) is not null));
                        return "ok";
                    },
                    tool.Id));

            _ = await session.RunTurnAsync("how much?", Ct);

            Assert.Equal([("B2", true)], seen);
        }

        // The tool gets the replacement, the stored call keeps what the model sent.
        [Fact]
        public async Task ReplacedArgumentsLeaveTheModelsCallRecordAsSent()
        {
            RecordingConversationStore store = new();
            ToolCallingChatClient model = new("final", new Dictionary<string, object?>(StringComparer.Ordinal) { ["sku"] = "A1" });
            Gate hook = new(before: gate => gate.ReplaceArguments(new Dictionary<string, object?>(StringComparer.Ordinal) { ["sku"] = "B2" }));
            ConversationSession session = HookSessions.Create(
                HookSessions.ToolAgentYaml, model, [hook], store, tools: tool => AIFunctionFactory.Create((string sku) => sku, tool.Id));

            _ = await session.RunTurnAsync("how much?", Ct);
            await session.FlushTranscriptAsync();

            FunctionCallContent call = Assert.Single(store.Rows.SelectMany(row => row.Content.Contents).OfType<FunctionCallContent>());
            Assert.Equal("A1", call.Arguments!["sku"]?.ToString());
            Assert.Contains(model.ToolResults, result => result.Contains("B2", StringComparison.Ordinal));
        }

        // Hooks and tools share one per-turn dictionary, through ToolCallScope.Items.
        [Fact]
        public async Task AHookAndAToolShareTheTurnsItems()
        {
            List<object?> seenByTool = [];
            ToolCallingChatClient model = new("final");
            Gate hook = new(before: gate => gate.Items["from"] = "hook");
            ConversationSession session = HookSessions.Create(
                HookSessions.ToolAgentYaml,
                model,
                [hook],
                tools: tool => AIFunctionFactory.Create(
                    (AIFunctionArguments arguments) =>
                    {
                        seenByTool.Add(ToolCallScopes.From(TurnInvocation.FiledIn(arguments)!).Items!["from"]);
                        return "ok";
                    },
                    tool.Id));

            _ = await session.RunTurnAsync("how much?", Ct);

            Assert.Equal(["hook"], seenByTool);
        }

        // The model sees the second attempt's result.
        [Fact]
        public async Task AFailedToolCanBeRetriedAndTheModelReadsTheRetry()
        {
            int attempts = 0;
            ToolCallingChatClient model = new("final");
            Gate hook = new(failed: gate => gate.Retry());
            RecordingHook notices = new();
            ConversationSession session = HookSessions.Create(
                HookSessions.ToolAgentYaml,
                model,
                [hook, notices],
                tools: tool => AIFunctionFactory.Create(
                    () => ++attempts == 1 ? throw new InvalidOperationException("first attempt") : "second attempt",
                    tool.Id));

            _ = await session.RunTurnAsync("how much?", Ct);
            await session.FlushNoticesAsync();

            Assert.Equal(2, attempts);
            Assert.Contains(model.ToolResults, result => result.Contains("second attempt", StringComparison.Ordinal));
            Assert.Equal(ToolOutcome.Ok, Assert.Single(notices.Of<ToolCalled>()).Outcome);
        }

        // Retry is capped at 2 per call.
        [Fact]
        public async Task RetryIsCappedAtTwo()
        {
            int attempts = 0;
            ToolCallingChatClient model = new("final");
            Gate hook = new(failed: gate => gate.Retry());
            ConversationSession session = HookSessions.Create(
                HookSessions.ToolAgentYaml,
                model,
                [hook],
                tools: tool => AIFunctionFactory.Create(
                    string () => { attempts++; throw new InvalidOperationException("always"); },
                    tool.Id));

            _ = await session.RunTurnAsync("how much?", Ct);

            Assert.Equal(3, attempts);
        }

        // CanRetry is false on the last attempt the cap allows, so a hook that checks it
        // reaches its own answer.
        [Fact]
        public async Task CanRetryIsFalseOnTheLastAttempt()
        {
            int attempts = 0;
            List<bool> canRetry = [];
            ToolCallingChatClient model = new("final");
            Gate hook = new(failed: gate =>
            {
                canRetry.Add(gate.CanRetry);
                if (gate.CanRetry)
                {
                    gate.Retry();
                }
                else
                {
                    gate.Respond("give up");
                }
            });
            ConversationSession session = HookSessions.Create(
                HookSessions.ToolAgentYaml,
                model,
                [hook],
                tools: tool => AIFunctionFactory.Create(
                    string () => { attempts++; throw new InvalidOperationException("always"); },
                    tool.Id));

            _ = await session.RunTurnAsync("how much?", Ct);

            Assert.Equal([true, true, false], canRetry);
            Assert.Equal(3, attempts);
            Assert.Contains(model.ToolResults, result => result.Contains("give up", StringComparison.Ordinal));
        }

        // ReplaceResult changes what the model reads; EndLoop stops the loop after the call.
        [Fact]
        public async Task AnAfterToolHookReplacesTheResultAndCanEndTheLoop()
        {
            StubToolBuilder stub = new("""{ "price": 50 }""");
            ToolCallingChatClient replaced = new("final");
            ConversationSession session = HookSessions.Create(
                HookSessions.ToolAgentYaml, replaced, [new Gate(after: gate => gate.ReplaceResult("redacted"))], tools: stub.Create);
            _ = await session.RunTurnAsync("how much?", Ct);

            ToolCallingChatClient ended = new("final");
            ConversationSession second = HookSessions.Create(
                HookSessions.ToolAgentYaml, ended, [new Gate(after: gate => gate.EndLoop())], tools: new StubToolBuilder("{}").Create);
            _ = await second.RunTurnAsync("how much?", Ct);

            Assert.Contains(replaced.ToolResults, result => result.Contains("redacted", StringComparison.Ordinal));
            Assert.Equal(1, ended.Calls);
        }

        // A hook that fails closed after the tool ran withholds the tool's result from the model.
        [Fact]
        public async Task AnAfterToolHookThatFailsClosedWithholdsTheResult()
        {
            StubToolBuilder stub = new("""{ "price": 50 }""");
            ToolCallingChatClient model = new("final");
            ConversationSession session = HookSessions.Create(HookSessions.ToolAgentYaml, model, [new ClosedAfterTool()], tools: stub.Create);

            _ = await session.RunTurnAsync("how much?", Ct);

            Assert.Equal(["price_lookup"], stub.Called);
            string result = Assert.Single(model.ToolResults);
            Assert.Contains("a hook withheld this result.", result, StringComparison.Ordinal);
            Assert.DoesNotContain("50", result, StringComparison.Ordinal);
        }

        private sealed class ClosedAfterTool : AgentHook
        {
            public override HookFailure FailureFor(GatePoint point) => HookFailure.Closed;

            public override ValueTask AfterToolAsync(ToolResultGate gate, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("boom");
        }

        private sealed class Gate(
            Action<ToolGate>? before = null,
            Action<ToolResultGate>? after = null,
            Action<ToolFailureGate>? failed = null) : AgentHook
        {
            public override ValueTask BeforeToolAsync(ToolGate gate, CancellationToken cancellationToken)
            {
                before?.Invoke(gate);
                return default;
            }

            public override ValueTask AfterToolAsync(ToolResultGate gate, CancellationToken cancellationToken)
            {
                after?.Invoke(gate);
                return default;
            }

            public override ValueTask AfterToolFailedAsync(ToolFailureGate gate, CancellationToken cancellationToken)
            {
                failed?.Invoke(gate);
                return default;
            }
        }
    }
}
