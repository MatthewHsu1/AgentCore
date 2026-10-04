using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Gates;
using AgentCore.Application.Hooks.Layers;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Runtime;
using AgentCore.Domain;
using AgentCore.TestSupport;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;
using AgentCore.Application.Runtime.Turn;

namespace AgentCore.Application.Tests.Hooks
{
    public sealed class ModelGateTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // The layer names the conversation and turn on every round, streaming or not.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ABeforeModelHookSeesEveryRoundOfItsTurn(bool streaming)
        {
            List<(string? Conversation, int? Turn, int Round)> seen = [];
            Gates hook = new(before: gate => seen.Add((gate.Scope.ConversationId, gate.Scope.TurnIndex, gate.Round)));
            ToolCallingChatClient model = new("final");
            ConversationSession session = HookSessions.Create(HookSessions.ToolAgentYaml, model, [hook], tools: new StubToolBuilder("{}").Create);

            if (streaming)
            {
                await foreach (ChatResponseUpdate _ in session.RunTurnStreamingAsync("how much?", Ct))
                {
                }
            }
            else
            {
                _ = await session.RunTurnAsync("how much?", Ct);
            }

            Assert.Equal([(session.ConversationId, 0, 0), (session.ConversationId, 0, 1)], seen);
        }

        [Fact]
        public async Task ReplacedMessagesAreWhatTheModelSees()
        {
            ScriptedChatClient reply = new("ok");
            RequestCapturingChatClient capture = new(reply);
            Gates hook = new(before: gate => gate.ReplaceMessages([.. gate.Messages, new ChatMessage(ChatRole.System, "be brief")]));
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, capture, [hook]);

            _ = await session.RunTurnAsync("hi", Ct);

            Assert.Contains(capture.Requests[0], message => message.Text == "be brief");
        }

        // Buffering a streamed round keeps the tool loop and the text.
        [Fact]
        public async Task AnAfterModelHookKeepsTheToolLoopOfAStreamedTurn()
        {
            StubToolBuilder stub = new("""{ "price": 50 }""");
            ToolCallingChatClient model = new("final");
            Gates hook = new(after: _ => { });
            ConversationSession session = HookSessions.Create(HookSessions.ToolAgentYaml, model, [hook], tools: stub.Create);

            string text = string.Empty;
            await foreach (ChatResponseUpdate update in session.RunTurnStreamingAsync("how much?", Ct))
            {
                text += update.Text;
            }

            Assert.Equal(["price_lookup"], stub.Called);
            Assert.Equal("final", text);
        }

        [Fact]
        public async Task AnAfterModelHookReplacesTheAnswer()
        {
            ScriptedChatClient reply = new("original");
            Gates hook = new(after: gate => gate.ReplaceResponse(new ChatResponse(new ChatMessage(ChatRole.Assistant, "rewritten"))));
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, reply, [hook]);

            TurnResult turn = await session.RunTurnAsync("hi", Ct);

            Assert.Equal("rewritten", turn.ReplyText);
        }

        // One notice per round trip, with the model's usage.
        [Fact]
        public async Task EachRoundIsOneModelCalledNoticeWithItsUsage()
        {
            RecordingHook notices = new();
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, new UsageModel(), [notices]);

            _ = await session.RunTurnAsync("hi", Ct);
            await session.FlushNoticesAsync();

            ModelCalled called = Assert.Single(notices.Of<ModelCalled>());
            Assert.Equal(("test-model", 0, 11L, 3L, 5L, "stop"), (called.ModelId, called.Round, called.InputTokens, called.OutputTokens, called.CachedInputTokens, called.FinishReason));
            Assert.Null(called.Failure);
        }

        // MAF mutates run options in place and LoopAgent reuses one options object, so a layer added per run
        // would stack. Each run here is a tool round and a text round: four rounds, four notices.
        [Fact]
        public async Task TwoRunsWithOneOptionsObjectAreOneNoticePerRound()
        {
            RecordingHook notices = new();
            await using HookRuntime runtime = HookRuntime.Create([notices], loggers: null);
            AIFunction tool = AIFunctionFactory.Create(() => "50", "price_lookup");
            ToolCallingChatClient model = new("final");
            ChatClientAgent inner = new(
                new ModelHookChatClient(model, runtime),
                new ChatClientAgentOptions { ChatOptions = new ChatOptions { Tools = [tool] } });
            AIAgent agent = ToolHookMiddleware.Apply(inner, runtime);
            SessionHooks session = new(runtime, "c1", "main", TimeProvider.System);
            TurnInvocation turn = new()
            {
                ConversationId = "c1",
                TurnIndex = 0,
                Stage = string.Empty,
                Hooks = session,
                Rounds = new TurnRounds(),
            };
            AgentRunOptions options = turn.RunOptions();

            _ = await agent.RunAsync("how much?", session: null, options, Ct);
            _ = await agent.RunAsync("how much?", session: null, options, Ct);
            await session.FlushAsync();
            session.Release();

            Assert.Equal(4, model.Calls);
            Assert.Equal([0, 1, 2, 3], notices.Of<ModelCalled>().Select(called => called.Round));
        }

        // An unbuffered round (no AfterModel hook) still replays a before-model hook's answer to the caller.
        [Fact]
        public async Task AnUnbufferedRespondStreamsTheHooksText()
        {
            ToolCallingChatClient model = new("never");
            BeforeOnly hook = new(gate => gate.Respond(new ChatResponse(new ChatMessage(ChatRole.Assistant, "canned"))));
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, model, [hook]);

            string text = string.Empty;
            await foreach (ChatResponseUpdate update in session.RunTurnStreamingAsync("hi", Ct))
            {
                text += update.Text;
            }

            Assert.Equal("canned", text);
            Assert.Equal(0, model.Calls);
        }

        // A before-model hook's answer that calls a tool reaches the tool loop: the tool runs, then the model answers the next round.
        [Fact]
        public async Task AnUnbufferedRespondCanCallATool()
        {
            StubToolBuilder stub = new("""{ "price": 50 }""");
            ToolCallingChatClient model = new("final");
            BeforeOnly hook = new(gate =>
            {
                if (gate.Round == 0)
                {
                    gate.Respond(new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call_1", "price_lookup")])));
                }
            });
            ConversationSession session = HookSessions.Create(HookSessions.ToolAgentYaml, model, [hook], tools: stub.Create);

            string text = string.Empty;
            await foreach (ChatResponseUpdate update in session.RunTurnStreamingAsync("how much?", Ct))
            {
                text += update.Text;
            }

            Assert.Equal(["price_lookup"], stub.Called);
            Assert.Equal(1, model.Calls);
            Assert.Equal("final", text);
        }

        // A hook that fails closed before the model refuses the round with the entry's refusal reply, and the model is not called.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ABeforeModelHookThatFailsClosedRefusesTheRoundWithoutCallingTheModel(bool streaming)
        {
            ToolCallingChatClient model = new("never");
            ConversationSession session = HookSessions.Create(RefusingYaml, model, [new ClosedModelHook(before: true)]);

            string text = await ReplyAsync(session, streaming);

            Assert.Equal("not through this entry.", text);
            Assert.Equal(0, model.Calls);
        }

        // A hook that fails closed after the model replaces the model's answer with the refusal reply.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task AnAfterModelHookThatFailsClosedReplacesTheAnswerWithTheRefusalReply(bool streaming)
        {
            ToolCallingChatClient model = new("the model's own answer");
            ConversationSession session = HookSessions.Create(RefusingYaml, model, [new ClosedModelHook(before: false)]);

            string text = await ReplyAsync(session, streaming);

            Assert.Equal("not through this entry.", text);
            Assert.Equal(1, model.Calls);
        }

        private const string RefusingYaml = """
        apiVersion: agentcore/v1
        refusalReply: "not through this entry."
        agents:
          defaults: { clock: false }
          items:
            - { id: only, instructions: "ok" }
        entries:
          main:
            agent: only
        """;

        private static async Task<string> ReplyAsync(ConversationSession session, bool streaming)
        {
            if (!streaming)
            {
                return (await session.RunTurnAsync("hi", Ct)).ReplyText;
            }

            string text = string.Empty;
            await foreach (ChatResponseUpdate update in session.RunTurnStreamingAsync("hi", Ct))
            {
                text += update.Text;
            }

            return text;
        }

        private sealed class ClosedModelHook(bool before) : AgentHook
        {
            public override HookFailure FailureFor(GatePoint point) => HookFailure.Closed;

            public override ValueTask BeforeModelAsync(ModelGate gate, CancellationToken cancellationToken) =>
                before ? throw new InvalidOperationException("boom") : default;

            public override ValueTask AfterModelAsync(ModelResultGate gate, CancellationToken cancellationToken) =>
                before ? default : throw new InvalidOperationException("boom");
        }

        internal sealed class BeforeOnly(Action<ModelGate> before) : AgentHook
        {
            public override ValueTask BeforeModelAsync(ModelGate gate, CancellationToken cancellationToken)
            {
                before(gate);
                return default;
            }
        }

        internal sealed class Gates(Action<ModelGate>? before = null, Action<ModelResultGate>? after = null) : AgentHook
        {
            public override ValueTask BeforeModelAsync(ModelGate gate, CancellationToken cancellationToken)
            {
                before?.Invoke(gate);
                return default;
            }

            public override ValueTask AfterModelAsync(ModelResultGate gate, CancellationToken cancellationToken)
            {
                if (after is null)
                {
                    return default;
                }

                after(gate);
                return default;
            }
        }
    }
}
