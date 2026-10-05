using AgentCore.Application.Hooks;
using Xunit;

namespace AgentCore.Application.Tests.Hooks
{
    public sealed class GatePointTests
    {
        // Deadline and default failure F of each of the 13 gates.
        public static TheoryData<GatePoint, double, HookFailure> Points => new()
        {
            { GatePoint.BeforeCall, 5, HookFailure.Closed },
            { GatePoint.BeforeEntry, 2, HookFailure.Closed },
            { GatePoint.BeforeTurn, 2, HookFailure.Open },
            { GatePoint.BeforeRun, 2, HookFailure.Open },
            { GatePoint.BeforeCompaction, 5, HookFailure.Open },
            { GatePoint.BeforeModel, 1, HookFailure.Open },
            { GatePoint.AfterModel, 1, HookFailure.Open },
            { GatePoint.AfterModelFailed, 1, HookFailure.Open },
            { GatePoint.BeforeToolApproval, 2, HookFailure.Open },
            { GatePoint.BeforeTool, 2, HookFailure.Open },
            { GatePoint.AfterTool, 2, HookFailure.Open },
            { GatePoint.AfterToolFailed, 2, HookFailure.Open },
            { GatePoint.AfterRun, 2, HookFailure.Open },
        };

        [Theory]
        [MemberData(nameof(Points))]
        public void EachPointHasTheSpecsDeadlineAndDefault(GatePoint point, double seconds, HookFailure failure)
        {
            Assert.Equal(TimeSpan.FromSeconds(seconds), point.Deadline);
            Assert.Equal(failure, point.DefaultFailure);
        }

        [Fact]
        public void TheSetHoldsThirteenPoints()
        {
            Assert.Equal(13, GatePoint.All.Count);
            Assert.Equal(13, GatePoint.All.Select(point => point.Name).Distinct(StringComparer.Ordinal).Count());
        }

        [Fact]
        public void AHookThatOverridesNothingFailsTheDefaultWay()
        {
            Assert.Equal(HookFailure.Closed, new NothingHook().FailureFor(GatePoint.BeforeCall));
            Assert.Equal(HookFailure.Open, new NothingHook().FailureFor(GatePoint.BeforeTool));
            Assert.Equal(TimeSpan.FromSeconds(30), new NothingHook().NoticeTimeout);
        }

        private sealed class NothingHook : AgentHook;
    }
}
