using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Domain;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Hooks
{
    /// <summary>
    /// Approval hooks over many calls at once. MAF hands back one approval request per run and asks the rule again about every queued
    /// request on every run, so the hooks must still decide each call once, and the deny layer's round cap must count
    /// model rounds, not hand-backs.
    /// </summary>
    public sealed class ApprovalDenialRoundsTests
    {
        private const string MixedYaml = """
        apiVersion: agentcore/v1
        tools:
          - { id: wire_money, kind: builtin, uses: test.wire, description: "Wire money." }
          - { id: archive, kind: builtin, uses: test.archive, description: "Archive the thread." }
          - { id: send_email, kind: builtin, uses: test.send, description: "Send an email." }
        agents:
          items:
            - id: only
              instructions: "do it all"
              tools: [ wire_money, archive, send_email ]
              approval: { auto: [ archive ] }
        entries:
          main:
            agent: only
        """;

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // h0 is left to a human and surfaces first, so MAF queues d2 and d3 across the turn the human answers in. The third
        // turn reads the record the first two wrote.
        [Fact]
        public async Task EachCallOfAMixedRoundIsDecidedOnceAcrossTwoTurns()
        {
            ParallelToolChatClient model = new("done.", ("wire_money", "h0"), ("archive", "a1"), ("send_email", "d2"), ("send_email", "d3"));
            ApprovalTools tools = new();
            DecidingHook hook = new(gate =>
            {
                if (gate.CallId.StartsWith('d'))
                {
                    gate.Deny("no");
                }
            });
            RecordingHook notices = new();
            RecordingConversationStore store = new();
            ConversationSession session = tools.Session(MixedYaml, [hook, notices], model, store: store);

            TurnResult asked = await session.RunTurnAsync("do it all", Ct);
            PendingApproval pending = Assert.Single(asked.Approvals);
            TurnResult answered = await session.RunTurnMessageAsync(session.TryCreateApprovalAnswer(pending.RequestId, approved: true)!, Ct);
            TurnResult next = await session.RunTurnAsync("thanks", Ct);
            await session.FlushNoticesAsync();
            await session.FlushTranscriptAsync();

            Assert.Equal(("done.", "done."), (answered.ReplyText, next.ReplyText));

            // No person was asked about d2 and d3, so the record keeps only h0's request (and a1's, which MAF approved).
            Assert.Equal(
                ["a1", "h0"],
                store.Rows.SelectMany(row => row.Content.Contents).OfType<ToolApprovalRequestContent>().Select(request => request.ToolCall.CallId).Order(StringComparer.Ordinal));
            Assert.Equal(["a1", "d2", "d3", "h0"], hook.Asked.Order(StringComparer.Ordinal));
            Assert.Equal((1, 1, 0), (tools.Ran("wire_money"), tools.Ran("archive"), tools.Sent));
            Assert.Equal(
                [
                    ("a1", ApprovalState.Approved, ApprovalBy.Rule),
                    ("d2", ApprovalState.Denied, ApprovalBy.Hook),
                    ("d3", ApprovalState.Denied, ApprovalBy.Hook),
                    ("h0", ApprovalState.Asked, ApprovalBy.Human),
                    ("h0", ApprovalState.Approved, ApprovalBy.Human),
                ],
                notices.Of<ApprovalChanged>().Select(changed => (changed.CallId, changed.State, changed.By)));
            Assert.Equal(
                [("a1", ToolOutcome.Ok), ("d2", ToolOutcome.Blocked), ("d3", ToolOutcome.Blocked), ("h0", ToolOutcome.Ok)],
                notices.Of<ToolCalled>().Select(called => (called.CallId, called.Outcome)).Order());
        }

        // d0 surfaces first, so MAF queues h1 and asks the rule about it again when the deny layer answers d0.
        [Fact]
        public async Task ACallLeftToAHumanIsDecidedOnceWhileItWaitsInTheQueue()
        {
            ParallelToolChatClient model = new("done.", ("send_email", "d0"), ("wire_money", "h1"));
            ApprovalTools tools = new();
            DecidingHook hook = new(gate =>
            {
                if (gate.CallId.StartsWith('d'))
                {
                    gate.Deny("no");
                }
            });
            RecordingHook notices = new();
            ConversationSession session = tools.Session(MixedYaml, [hook, notices], model);

            TurnResult turn = await session.RunTurnAsync("do it all", Ct);
            await session.FlushNoticesAsync();

            Assert.Equal(["d0", "h1"], hook.Asked.Order(StringComparer.Ordinal));
            Assert.Equal("wire_money", Assert.Single(turn.Approvals).ToolName);
            Assert.Equal(
                [("d0", ApprovalState.Denied), ("h1", ApprovalState.Asked)],
                notices.Of<ApprovalChanged>().Select(changed => (changed.CallId, changed.State)));
        }

        // Ten denials are ten hand-backs and one model round, far below the cap: every call is refused and the model answers.
        // The next turn reads the record this one wrote.
        [Fact]
        public async Task TenParallelDenialsAreEachDecidedOnceAndNeverReachAHuman()
        {
            ParallelToolChatClient model = new("done.", [.. Enumerable.Range(0, 10).Select(index => ("send_email", $"d{index}"))]);
            ApprovalTools tools = new();
            DecidingHook hook = new(gate => gate.Deny("no"));
            RecordingHook notices = new();
            ConversationSession session = tools.Session(ApprovalTools.GatedYaml, [hook, notices], model);

            TurnResult turn = await session.RunTurnAsync("send them", Ct);
            TurnResult next = await session.RunTurnAsync("thanks", Ct);
            await session.FlushNoticesAsync();

            Assert.Equal(("done.", "done."), (turn.ReplyText, next.ReplyText));
            Assert.Empty(turn.Approvals);
            Assert.Equal(0, tools.Sent);
            Assert.Equal(10, hook.Asked.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(10, hook.Asked.Count);
            Assert.All(notices.Of<ApprovalChanged>(), changed => Assert.Equal((ApprovalState.Denied, ApprovalBy.Hook), (changed.State, changed.By)));
            Assert.Equal(10, notices.Of<ApprovalChanged>().Count);
            Assert.Equal(10, notices.Of<ToolCalled>().Count(called => called.Outcome == ToolOutcome.Blocked));
            Assert.Equal(3, model.Calls);
        }

        // At the cap a hook's denial still never reaches a person; the turn ends as a fault and speaks the fallback.
        [Fact]
        public async Task AModelThatKeepsCallingADeniedToolEndsInTheFallbackWithoutAskingAnyone()
        {
            TurnScriptChatClient model = TurnScriptChatClient.ToolForever("send_email");
            ApprovalTools tools = new();
            RecordingHook notices = new();
            ConversationSession session = tools.Session(ApprovalTools.GatedYaml, [new DecidingHook(gate => gate.Deny("no")), notices], model);

            TurnResult turn = await session.RunTurnAsync("send it", Ct);
            await session.FlushNoticesAsync();

            Assert.Equal(0, tools.Sent);
            Assert.Empty(turn.Approvals);
            Assert.NotNull(turn.Failure);
            Assert.Equal(session.Compiled.FallbackReply, turn.ReplyText);
            Assert.DoesNotContain(notices.Of<ApprovalChanged>(), changed => changed.By == ApprovalBy.Human);
            Assert.Equal(FaultKind.DenialRoundsExhausted, Assert.Single(notices.Of<Fault>()).Kind);

            // The first round plus the eight rounds of denials the layer answers.
            Assert.Equal(9, model.Calls);
        }
    }
}
