using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Gates;
using AgentCore.Application.Hooks.Layers;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Domain;
using AgentCore.TestSupport;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;
using AgentCore.Application.Runtime.Turn;

namespace AgentCore.Application.Tests.Hooks
{
    public sealed class ModelFailureGateTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // A throw before any update is retried once; the caller gets the text exactly once.
        [Fact]
        public async Task AThrowBeforeAnyUpdateIsRetriedCleanly()
        {
            FlakyModel model = new(attempt => attempt == 0 ? FlakyModel.Fault.BeforeFirstUpdate : FlakyModel.Fault.None);
            Retrier hook = new();
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, model, [hook]);

            string text = string.Empty;
            await foreach (ChatResponseUpdate update in session.RunTurnStreamingAsync("hi", Ct))
            {
                text += update.Text;
            }

            Assert.Equal("final text", text);
            Assert.Equal(2, model.Attempts);
            Assert.Equal([true], hook.CanRetry);
        }

        // After one yielded update a retry would duplicate text ("finalfinal text"), so Retry throws and
        // the hook fails open: the fault reaches the turn.
        [Fact]
        public async Task ARetryAfterAYieldedUpdateIsRefused()
        {
            FlakyModel model = new(_ => FlakyModel.Fault.AfterFirstUpdate);
            Retrier hook = new();
            RecordingHook notices = new();
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, model, [hook, notices]);

            TurnResult turn = await session.RunTurnAsync("hi", Ct);
            await session.FlushNoticesAsync();

            Assert.Equal(1, model.Attempts);
            Assert.Equal([false], hook.CanRetry);
            Assert.Equal(ConversationSession.FallbackReply, turn.ReplyText);
            Assert.Contains(notices.Of<Fault>(), fault => fault.Kind == FaultKind.HookFailed);
            Assert.NotNull(Assert.Single(notices.Of<ModelCalled>()).Failure);
        }

        // Retries are capped at 2 per call.
        [Fact]
        public async Task RetryIsCappedAtTwo()
        {
            FlakyModel model = new(_ => FlakyModel.Fault.BeforeFirstUpdate);
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, model, [new Retrier()]);

            _ = await session.RunTurnAsync("hi", Ct);

            Assert.Equal(3, model.Attempts);
        }

        // After part of the round reached the caller, the hook's answer follows it as
        // its own message, the shape the fallback gives a dropped stream: assistant "final", then the fallback line.
        [Fact]
        public async Task AnAnswerAfterAPartialStreamIsItsOwnMessage()
        {
            FlakyModel model = new(attempt => attempt == 0 ? FlakyModel.Fault.AfterFirstUpdate : FlakyModel.Fault.None);
            RequestCapturingChatClient capture = new(model);
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, capture, [new Responder()]);

            TurnResult turn = await session.RunTurnAsync("hi", Ct);
            _ = await session.RunTurnAsync("again", Ct);

            Assert.Equal("from the hook", turn.ReplyText);
            Assert.Equal(["final", "from the hook"], capture.Requests[^1].Where(message => message.Role == ChatRole.Assistant).Select(message => message.Text));
        }

        // CanRetry is false on the last attempt the cap allows, so a hook that checks it
        // reaches its fallback answer.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task CanRetryIsFalseOnTheLastAttempt(bool streaming)
        {
            FlakyModel model = new(_ => FlakyModel.Fault.BeforeFirstUpdate);
            GivingUp hook = new();

            string reply;
            if (streaming)
            {
                ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, model, [hook]);
                reply = (await session.RunTurnAsync("hi", Ct)).ReplyText;
            }
            else
            {
                await using HookRuntime runtime = HookRuntime.Create([hook], loggers: null);
                ChatClientAgent agent = new(new ModelHookChatClient(model, runtime), new ChatClientAgentOptions { Name = "bare" });
                SessionHooks session = new(runtime, "c1", "main", TimeProvider.System);
                TurnInvocation turn = new() { ConversationId = "c1", TurnIndex = 0, Stage = string.Empty, Hooks = session, Rounds = new TurnRounds() };
                reply = (await agent.RunAsync("hi", options: turn.RunOptions(), cancellationToken: Ct)).Text;
                session.Release();
            }

            Assert.Equal([true, true, false], hook.CanRetry);
            Assert.Equal("give up", reply);
            Assert.Equal(3, model.Attempts);
        }

        private sealed class Retrier : AgentHook
        {
            public List<bool> CanRetry { get; } = [];

            public override ValueTask AfterModelFailedAsync(ModelFailureGate gate, CancellationToken cancellationToken)
            {
                if (gate.Attempt == 0)
                {
                    CanRetry.Add(gate.CanRetry);
                }

                gate.Retry();
                return default;
            }
        }

        private sealed class GivingUp : AgentHook
        {
            public List<bool> CanRetry { get; } = [];

            public override ValueTask AfterModelFailedAsync(ModelFailureGate gate, CancellationToken cancellationToken)
            {
                CanRetry.Add(gate.CanRetry);
                if (gate.CanRetry)
                {
                    gate.Retry();
                }
                else
                {
                    gate.Respond(new ChatResponse(new ChatMessage(ChatRole.Assistant, "give up")));
                }

                return default;
            }
        }

        private sealed class Responder : AgentHook
        {
            public override ValueTask AfterModelFailedAsync(ModelFailureGate gate, CancellationToken cancellationToken)
            {
                gate.Respond(new ChatResponse(new ChatMessage(ChatRole.Assistant, "from the hook")));
                return default;
            }
        }
    }
}
