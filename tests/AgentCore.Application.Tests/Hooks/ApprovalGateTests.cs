using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Gates;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Sessions;
using AgentCore.Domain;
using AgentCore.TestSupport;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;
using AgentCore.Application.Runtime.Turn;

namespace AgentCore.Application.Tests.Hooks
{
    public sealed class ApprovalGateTests
    {
        // The document of ConversationSessionApprovalTests.cs:15-27 with an auto: pattern.
        private const string AutoYaml = """
        apiVersion: agentcore/v1
        tools:
          - { id: send_email, kind: builtin, uses: test.send, description: "Send an email." }
        agents:
          items:
            - id: only
              instructions: "send the mail"
              tools: [ send_email ]
              approval: { auto: [ send_email ] }
        entries:
          main:
            agent: only
        """;

        private const string TwoToolYaml = """
        apiVersion: agentcore/v1
        tools:
          - { id: send_email, kind: builtin, uses: test.send, description: "Send an email." }
          - { id: price_lookup, kind: builtin, uses: orders.read, description: "Look up the price of an item." }
        agents:
          items:
            - id: only
              instructions: "send the mail"
              tools: [ send_email, price_lookup ]
        entries:
          main:
            agent: only
        """;

        // What the model reads for a denied call, as for a human denial.
        private const string Rejected = "Tool call invocation rejected. ";

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact]
        public async Task AnApprovingHookRunsTheToolWithoutAsking()
        {
            ApprovalTools tools = new();
            RecordingHook notices = new();
            ConversationSession session = tools.Session(ApprovalTools.GatedYaml, [new DecidingHook(gate => gate.Approve()), notices]);

            TurnResult turn = await session.RunTurnAsync("send it", Ct);
            await session.FlushNoticesAsync();

            Assert.Equal(1, tools.Sent);
            Assert.Empty(turn.Approvals);
            Assert.Equal("done.", turn.ReplyText);
            ApprovalChanged changed = Assert.Single(notices.Of<ApprovalChanged>());
            Assert.Equal((ApprovalState.Approved, ApprovalBy.Hook, "send_email"), (changed.State, changed.By, changed.ToolName));
        }

        [Fact]
        public async Task ADenyingHookRejectsTheCallAndTheModelReadsTheReason()
        {
            ApprovalTools tools = new();
            RecordingHook notices = new();
            ConversationSession session = tools.Session(ApprovalTools.GatedYaml, [new DecidingHook(gate => gate.Deny("policy says no")), notices]);

            TurnResult turn = await session.RunTurnAsync("send it", Ct);
            await session.FlushNoticesAsync();

            Assert.Equal(0, tools.Sent);
            Assert.Empty(turn.Approvals);
            Assert.Contains(tools.Model.ToolResults, result => result.Contains(Rejected + "policy says no", StringComparison.Ordinal));
            ApprovalChanged changed = Assert.Single(notices.Of<ApprovalChanged>());
            Assert.Equal((ApprovalState.Denied, ApprovalBy.Hook, "policy says no"), (changed.State, changed.By, changed.Reason));
        }

        // With no decision the request still goes to a human.
        [Fact]
        public async Task NoDecisionStillAsksAHuman()
        {
            ApprovalTools tools = new();
            RecordingHook notices = new();
            ConversationSession session = tools.Session(ApprovalTools.GatedYaml, [new DecidingHook(_ => { }), notices]);

            TurnResult turn = await session.RunTurnAsync("send it", Ct);
            await session.FlushNoticesAsync();

            Assert.Equal(0, tools.Sent);
            _ = Assert.Single(turn.Approvals);
            ApprovalChanged changed = Assert.Single(notices.Of<ApprovalChanged>());
            Assert.Equal((ApprovalState.Asked, ApprovalBy.Human), (changed.State, changed.By));
        }

        [Fact]
        public async Task AnAutoPatternStillApprovesWhenNoHookDecides()
        {
            ApprovalTools tools = new();
            RecordingHook notices = new();
            ConversationSession session = tools.Session(AutoYaml, [new DecidingHook(_ => { }), notices]);

            _ = await session.RunTurnAsync("send it", Ct);
            await session.FlushNoticesAsync();

            Assert.Equal(1, tools.Sent);
            Assert.Equal(ApprovalBy.Rule, Assert.Single(notices.Of<ApprovalChanged>()).By);
        }

        // A hook's denial beats the document's auto: pattern.
        [Fact]
        public async Task AHookDenialBeatsAnAutoPattern()
        {
            ApprovalTools tools = new();
            ConversationSession session = tools.Session(AutoYaml, [new DecidingHook(gate => gate.Deny("not today"))]);

            TurnResult turn = await session.RunTurnAsync("send it", Ct);

            Assert.Equal(0, tools.Sent);
            Assert.Empty(turn.Approvals);
            Assert.Contains(tools.Model.ToolResults, result => result.Contains(Rejected + "not today", StringComparison.Ordinal));
        }

        // Closed applies the gate's safe verb and stops the chain, so the auto: pattern never approves.
        [Fact]
        public async Task AHookThatFailsClosedDeniesTheCall()
        {
            ApprovalTools tools = new();
            ConversationSession session = tools.Session(AutoYaml, [new FailsClosed()]);

            _ = await session.RunTurnAsync("send it", Ct);

            Assert.Equal(0, tools.Sent);
            Assert.Contains(tools.Model.ToolResults, result => result.Contains(Rejected + "a hook denied this call.", StringComparison.Ordinal));
        }

        // The deny layer runs the agent again with the caller's options: the call after the denial is still one notice,
        // and the denied call is a refused call, not an undeclared tool. TurnScriptChatClient gives call n the id "c{n}".
        [Fact]
        public async Task ADeniedCallIsBlockedAndTheCallAfterItIsOneNotice()
        {
            TurnScriptChatClient model = TurnScriptChatClient.ToolsThenText(["send_email", "price_lookup"], "done.");
            ApprovalTools tools = new();
            RecordingHook notices = new();
            ConversationSession session = tools.Session(TwoToolYaml, [new DecidingHook(gate => gate.Deny("no")), notices], model);

            TurnResult turn = await session.RunTurnAsync("send it", Ct);
            await session.FlushNoticesAsync();

            Assert.Equal("done.", turn.ReplyText);
            Assert.Equal((0, 1), (tools.Sent, tools.Ran("price_lookup")));
            Assert.Equal(
                [("c0", ToolOutcome.Blocked), ("c1", ToolOutcome.Ok)],
                notices.Of<ToolCalled>().Select(called => (called.CallId, called.Outcome)).Order());
            Assert.Equal(3, model.Calls);
            Assert.Equal(3, notices.Of<ModelCalled>().Count);
        }

        // A delegated agent tool runs its agent without streaming (DelegatedAgentRun), so the deny layer's other path.
        [Fact]
        public async Task ADenialHoldsOnARunThatDoesNotStream()
        {
            ApprovalTools tools = new();
            await using HookRuntime runtime = HookRuntime.Create([new DecidingHook(gate => gate.Deny("policy says no"))], loggers: null);
            SessionHooks hooks = new(runtime, "conversation", "main", TimeProvider.System);
            TurnInvocation turn = new() { ConversationId = "conversation", TurnIndex = 0, Stage = string.Empty, Hooks = hooks };
            ChatClientAgent inner = new(tools.Model, new ChatClientAgentOptions { ChatOptions = new ChatOptions { Tools = [tools.Send()] } });
            AIAgent agent = AgentApproval.Apply(inner, defaults: null, new AgentConfiguration { Id = "only", Instructions = "send" }, tools: null, runtime);

            AgentResponse response = await agent.RunAsync("send it", session: null, turn.RunOptions(), Ct);
            await hooks.FlushAsync();
            hooks.Release();

            Assert.Equal(0, tools.Sent);
            Assert.Equal("done.", response.Text);
            Assert.Contains(tools.Model.ToolResults, result => result.Contains(Rejected + "policy says no", StringComparison.Ordinal));
            Assert.Empty(response.Messages.SelectMany(message => message.Contents).OfType<ToolApprovalRequestContent>());
        }

        private sealed class FailsClosed : AgentHook
        {
            public override HookFailure FailureFor(GatePoint point) => HookFailure.Closed;

            public override ValueTask BeforeToolApprovalAsync(ApprovalGate gate, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("policy store down");
        }
    }
}
