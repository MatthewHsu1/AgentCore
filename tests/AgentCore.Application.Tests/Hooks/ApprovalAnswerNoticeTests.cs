using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Sessions.Memory;
using AgentCore.Application.Tests.Sessions;
using AgentCore.Domain;
using AgentCore.TestSupport;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Hooks
{
    /// <summary>A person's approval answer is raised when a turn takes it in, by whichever door, and never when it is only built.</summary>
    public sealed class ApprovalAnswerNoticeTests
    {
        // ToolCallingChatClient gives every call this id.
        private const string ModelCallId = "conversation_1";

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // The human path names the function call id too, so the two notices of one call join.
        [Fact]
        public async Task AHumansAnswerIsNamed()
        {
            ApprovalTools tools = new();
            RecordingHook notices = new();
            ConversationSession session = tools.Session(ApprovalTools.GatedYaml, [notices]);

            TurnResult asked = await session.RunTurnAsync("send it", Ct);
            ChatMessage? answer = session.TryCreateApprovalAnswer(asked.Approvals[0].RequestId, approved: true);
            _ = await session.RunTurnMessageAsync(answer!, Ct);
            await session.FlushNoticesAsync();

            Assert.Equal(
                [(ApprovalState.Asked, ApprovalBy.Human, ModelCallId), (ApprovalState.Approved, ApprovalBy.Human, ModelCallId)],
                notices.Of<ApprovalChanged>().Select(changed => (changed.State, changed.By, changed.CallId)));
        }

        // A refused call is a refusal, not an undeclared tool: one Blocked notice.
        [Fact]
        public async Task AHumansDenialBlocksTheCall()
        {
            ApprovalTools tools = new();
            RecordingHook notices = new();
            ConversationSession session = tools.Session(ApprovalTools.GatedYaml, [notices]);

            TurnResult asked = await session.RunTurnAsync("send it", Ct);
            _ = await session.RunTurnMessageAsync(session.TryCreateApprovalAnswer(asked.Approvals[0].RequestId, approved: false)!, Ct);
            await session.FlushNoticesAsync();

            Assert.Equal(0, tools.Sent);
            ToolCalled called = Assert.Single(notices.Of<ToolCalled>());
            Assert.Equal((ModelCallId, ToolOutcome.Blocked), (called.CallId, called.Outcome));
            Assert.Equal(ApprovalState.Denied, notices.Of<ApprovalChanged>()[^1].State);
        }

        [Fact]
        public async Task ABuiltAnswerWhoseTurnIsRefusedRaisesNothing()
        {
            ApprovalTools tools = new();
            RecordingHook notices = new();
            ConversationSession session = tools.Session(ApprovalTools.GatedYaml, [notices]);

            TurnResult asked = await session.RunTurnAsync("send it", Ct);
            ChatMessage answer = session.TryCreateApprovalAnswer(asked.Approvals[0].RequestId, approved: true)!;
            await session.DisposeAsync();
            _ = await Assert.ThrowsAnyAsync<Exception>(() => session.RunTurnMessageAsync(answer, Ct));
            await session.FlushNoticesAsync();

            Assert.Equal(0, tools.Sent);
            Assert.Equal(ApprovalState.Asked, Assert.Single(notices.Of<ApprovalChanged>()).State);
        }

        // AgentCoreAgent takes the caller's own ToolApprovalResponseContent straight into the run; no answer is built.
        [Fact]
        public async Task AnAnswerPassedStraightToTheAgentIsRaisedOnce()
        {
            ApprovalTools tools = new();
            RecordingHook notices = new();
            CompiledAgent compiled = HookSessions.Compile(ApprovalTools.GatedYaml, tools.Model, [notices], tools: tools.Tool)["main"];
            ConversationSessionFactory factory = new(compiled, new GuardEvaluator(compiled.Configuration.Guards));
            using InMemoryConversationSessions sessions = new(
                new Dictionary<string, IConversationSessionFactory>(StringComparer.Ordinal) { ["main"] = factory },
                TimeSpan.FromMinutes(30),
                TimeProvider.System);
            AgentCoreAgent agent = new(sessions, "main");
            AgentSession session = await agent.CreateSessionAsync(Ct);

            List<AgentResponseUpdate> first = [];
            await foreach (AgentResponseUpdate update in agent.RunStreamingAsync("send it", session, cancellationToken: Ct))
            {
                first.Add(update);
            }

            ToolApprovalRequestContent request = first.SelectMany(update => update.Contents).OfType<ToolApprovalRequestContent>().Single();
            _ = await agent.RunAsync(new ChatMessage(ChatRole.User, [request.CreateResponse(true)]), session, cancellationToken: Ct);
            _ = await notices.WaitForAsync<TurnCompleted>(completed => completed.Scope.TurnIndex == 1).WaitAsync(TimeSpan.FromSeconds(10), Ct);

            Assert.Equal(1, tools.Sent);
            Assert.Equal(
                [(ApprovalState.Asked, ModelCallId), (ApprovalState.Approved, ModelCallId)],
                notices.Of<ApprovalChanged>().Select(changed => (changed.State, changed.CallId)));
        }
    }
}
