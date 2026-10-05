using AgentCore.Application.Hooks.Notices;
using Xunit;
using AgentCore.Application.Runtime.Cut;
using AgentCore.Application.Runtime.Turn.Lifecycle;

namespace AgentCore.Application.Tests.Hooks
{
    public sealed class TurnOutcomesTests
    {
        // Blocked = moderation flagged; Cancelled = sealed with the stop cut (ShownText and Played both
        // null); Empty = no fault and no text; Fallback = a fault the fallback layer caught; Faulted = a fault that
        // escaped every layer to the seal; else Answered.
        [Theory]
        [InlineData(null, false, false, "none", "none", TurnOutcome.Answered)]
        [InlineData(null, false, false, "clean", "none", TurnOutcome.Answered)]
        [InlineData(null, false, false, "none", "partial", TurnOutcome.Answered)]
        [InlineData("empty_reply", false, false, "none", "none", TurnOutcome.Empty)]
        [InlineData(null, true, false, "none", "none", TurnOutcome.Fallback)]
        [InlineData("tool", true, true, "none", "none", TurnOutcome.Faulted)]
        [InlineData(null, false, false, "flagged", "none", TurnOutcome.Blocked)]
        [InlineData(null, false, false, "none", "stop", TurnOutcome.Cancelled)]
        public void EachSealedTurnReadsAsItsDecidedOutcome(
            string? failure, bool faulted, bool escaped, string moderation, string cut, TurnOutcome expected)
        {
            ReplyOutcome outcome = new(
                "words", "words", failure, ToolFault: null, InterruptedAfter: null, Approvals: [], Fault: faulted ? new InvalidOperationException("down") : null);
            TurnDisposition? disposition = moderation switch
            {
                "clean" => new TurnDisposition(ModerationOutcome.Clean, null, FallbackCause.None, null, null),
                "flagged" => new TurnDisposition(ModerationOutcome.Flagged, "violence", FallbackCause.None, null, null),
                _ => null,
            };
            TurnCut? turnCut = cut switch
            {
                "partial" => new TurnCut("shown so far", TimeSpan.FromSeconds(2)),
                "stop" => new TurnCut(null, null),
                _ => null,
            };

            Assert.Equal(expected, TurnOutcomes.Of(outcome, disposition, turnCut, escaped));
        }
    }
}
