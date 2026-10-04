using AgentCore.Application.Hooks.Layers;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Runtime;
using AgentCore.Application.Tools;
using AgentCore.Domain.Audit;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Hooks
{
    public sealed class ToolCalledNoticeTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact]
        public async Task AToolThatAnswersIsOneOkNotice()
        {
            RecordingHook hook = new();
            ToolCallingChatClient model = new("final");
            ConversationSession session = HookSessions.Create(HookSessions.ToolAgentYaml, model, [hook], tools: new StubToolBuilder("{}").Create);

            _ = await session.RunTurnAsync("how much?", Ct);
            await session.FlushNoticesAsync();

            ToolCalled called = Assert.Single(hook.Of<ToolCalled>());
            Assert.Equal(("price_lookup", ToolOutcome.Ok, false), (called.ToolName, called.Outcome, called.Fatal));
        }

        // A fault the model cannot answer is one Failed, fatal notice per call.
        [Fact]
        public async Task AnUnreachableEndpointIsOneFailedFatalNotice()
        {
            RecordingHook hook = new();
            using NamedToolCallingChatClient model = new("price_lookup", "Sorry.");
            ConversationSession session = HookSessions.Create(
                HookSessions.ToolAgentYaml, model, [hook], tools: new UnreachableEndpointToolBuilder().Create);

            _ = await session.RunTurnAsync("how much?", Ct);
            await session.FlushNoticesAsync();

            ToolCalled called = Assert.Single(hook.Of<ToolCalled>());
            Assert.Equal((ToolOutcome.Failed, true, ToolFailureKind.Faulted), (called.Outcome, called.Fatal, called.FailureKind));
            Assert.NotNull(called.Failure);
        }

        // Values from ConversationSessionAuditToolFailureTests.AHallucinatedToolName_ReachesTheChain.
        [Fact]
        public async Task AToolTheModelInventedIsOneUndeclaredNotice()
        {
            RecordingHook hook = new();
            using NamedToolCallingChatClient model = new("lookup_ordar", "Let me try that again.");
            ConversationSession session = HookSessions.Create(
                HookSessions.ToolAgentYaml, model, [hook], tools: new StubToolBuilder("{}").Create);

            _ = await session.RunTurnAsync("where is my order", Ct);
            await session.FlushNoticesAsync();

            ToolCalled called = Assert.Single(hook.Of<ToolCalled>());
            Assert.Equal(("lookup_ordar", ToolOutcome.Undeclared, ToolFailureKind.Undeclared), (called.ToolName, called.Outcome, called.FailureKind));
            Assert.Equal(model.CallIds[0], called.CallId);
        }

        [Fact]
        public async Task ATimeLimitMarksTheCallTimedOut()
        {
            AIFunction slow = AIFunctionFactory.Create(async (CancellationToken token) => { await Task.Delay(Timeout.Infinite, token); return "late"; }, "slow");
            TimeLimitedTool limited = new(slow, TimeSpan.FromMilliseconds(20));
            AIFunctionArguments arguments = new();

            _ = await limited.InvokeAsync(arguments, Ct);

            Assert.Equal(ToolOutcome.TimedOut, ToolCallOutcomes.Of(arguments));
        }

        [Fact]
        public async Task ACachedAnswerMarksTheCallCached()
        {
            ServiceCollection services = new();
            _ = services.AddHybridCache();
            HybridCache cache = services.BuildServiceProvider().GetRequiredService<HybridCache>();
            CachedTool cached = new(AIFunctionFactory.Create(() => "fresh", "lookup"), cache, TimeSpan.FromMinutes(1));

            AIFunctionArguments first = new();
            AIFunctionArguments second = new();
            _ = await cached.InvokeAsync(first, Ct);
            _ = await cached.InvokeAsync(second, Ct);

            Assert.Equal(ToolOutcome.Ok, ToolCallOutcomes.Of(first));
            Assert.Equal(ToolOutcome.Cached, ToolCallOutcomes.Of(second));
        }
    }
}
