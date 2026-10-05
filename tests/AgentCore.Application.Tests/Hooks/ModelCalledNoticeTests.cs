using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Gates;
using AgentCore.Application.Hooks.Layers;
using AgentCore.Application.Hooks.Notices;
using AgentCore.TestSupport;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;
using AgentCore.Application.Runtime.Turn;

namespace AgentCore.Application.Tests.Hooks
{
    /// <summary>One <see cref="ModelCalled"/> per round trip to the model, on both paths of the model layer.</summary>
    public sealed class ModelCalledNoticeTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // A round an AfterModelFailed hook answered still failed at the model, so its notice says how.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ARoundAFailureHookAnsweredCarriesTheModelsFailure(bool streaming)
        {
            FlakyModel model = new(_ => FlakyModel.Fault.BeforeFirstUpdate);
            RecordingHook notices = new();

            string reply = await RunAsync(streaming, model, [new Responder(), notices]);

            Assert.Equal("from the hook", reply);
            ModelCalled called = Assert.Single(notices.Of<ModelCalled>());
            Assert.Equal("InvalidOperationException: model down", called.Failure);
        }

        // A round a BeforeModel hook answered never reached the model: no round trip, no notice.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ARoundABeforeModelHookAnsweredIsNoModelCall(bool streaming)
        {
            FlakyModel model = new(_ => FlakyModel.Fault.None);
            RecordingHook notices = new();
            ModelGateTests.BeforeOnly answering = new(gate => gate.Respond(new ChatResponse(new ChatMessage(ChatRole.Assistant, "canned"))));

            string reply = await RunAsync(streaming, model, [answering, notices]);

            Assert.Equal("canned", reply);
            Assert.Equal(0, model.Attempts);
            Assert.Empty(notices.Of<ModelCalled>());
        }

        private static async Task<string> RunAsync(bool streaming, IChatClient model, AgentHook[] hooks)
        {
            if (streaming)
            {
                ConversationSession conversation = HookSessions.Create(HookSessions.OneAgentYaml, model, hooks);
                string text = (await conversation.RunTurnAsync("hi", Ct)).ReplyText;
                await conversation.FlushNoticesAsync();
                return text;
            }

            await using HookRuntime runtime = HookRuntime.Create(hooks, loggers: null);
            ChatClientAgent agent = new(new ModelHookChatClient(model, runtime), new ChatClientAgentOptions { Name = "bare" });
            SessionHooks session = new(runtime, "c1", "main", TimeProvider.System);
            TurnInvocation turn = new() { ConversationId = "c1", TurnIndex = 0, Stage = string.Empty, Hooks = session, Rounds = new TurnRounds() };
            string reply = (await agent.RunAsync("hi", options: turn.RunOptions(), cancellationToken: Ct)).Text;
            await session.FlushAsync();
            session.Release();
            return reply;
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
