using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Gates;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Hooks
{
    public sealed class HookGateTests
    {
        private static readonly HookScope Scope = new("c1", "main", 0, "", Guid.CreateVersion7(), 0, DateTimeOffset.UnixEpoch);

        private static ToolGate Tool()
        {
            return new(
            Scope,
            "price_lookup",
            "call-1",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["sku"] = "A1" },
            new Dictionary<string, object?>(StringComparer.Ordinal));
        }

        // Calling two terminal verbs throws.
        [Fact]
        public void TwoTerminalVerbsThrow()
        {
            ToolGate gate = Tool();
            gate.Block("no");

            _ = Assert.Throws<InvalidOperationException>(() => gate.Respond("yes"));
            Assert.True(gate.Blocked);
            Assert.Equal("no", gate.Result);
        }

        // A verb after a terminal verb throws.
        [Fact]
        public void AModifyingVerbAfterATerminalVerbThrows()
        {
            ToolGate gate = Tool();
            gate.Block("no");

            _ = Assert.Throws<InvalidOperationException>(gate.EndLoop);
            Assert.False(gate.EndsLoop);
        }

        [Fact]
        public void ModifyingVerbsBeforeATerminalVerbAllStand()
        {
            ToolGate gate = Tool();
            Dictionary<string, object?> patched = new(StringComparer.Ordinal) { ["sku"] = "B2" };

            gate.ReplaceArguments(patched);
            gate.EndLoop();
            gate.Respond("cached");

            Assert.Same(patched, gate.NewArguments);
            Assert.True(gate.EndsLoop);
            Assert.True(gate.Responded);
            Assert.True(gate.IsTerminal);
        }

        // Once the chain moves past a hook its view is sealed; a late verb throws and changes nothing.
        [Fact]
        public void ASealedViewRefusesEveryVerb()
        {
            ToolGate gate = Tool();
            gate.Seal();

            _ = Assert.Throws<InvalidOperationException>(() => gate.Block("late"));
            Assert.False(gate.Blocked);
            Assert.Null(gate.Result);
        }

        // Retry is allowed only before the first stream update was yielded.
        [Fact]
        public void ModelRetryAfterAYieldedUpdateThrows()
        {
            ModelFailureGate gate = new(Scope, round: 0, attempt: 0, new InvalidOperationException("boom"), canRetry: false, new Dictionary<string, object?>());

            _ = Assert.Throws<InvalidOperationException>(gate.Retry);
            Assert.False(gate.Retrying);
        }

        [Fact]
        public void ModelRetryBeforeAnyUpdateStands()
        {
            ModelFailureGate gate = new(Scope, round: 0, attempt: 0, new InvalidOperationException("boom"), canRetry: true, new Dictionary<string, object?>());

            gate.Retry();

            Assert.True(gate.Retrying);
        }

        [Fact]
        public void AnApprovalDenialKeepsItsReason()
        {
            ApprovalGate gate = new(Scope, "transfer", "call-9", new Dictionary<string, object?>(), new Dictionary<string, object?>());

            gate.Deny("over the limit");

            Assert.False(gate.Approved);
            Assert.Equal("over the limit", gate.DenyReason);
        }

        [Fact]
        public void AModelGateResponseIsTerminal()
        {
            ModelGate gate = new(Scope, round: 1, [new ChatMessage(ChatRole.User, "hi")], options: null, new Dictionary<string, object?>());

            gate.Respond(new ChatResponse(new ChatMessage(ChatRole.Assistant, "canned")));

            Assert.True(gate.IsTerminal);
            Assert.Equal("canned", gate.Response?.Text);
        }
    }
}
