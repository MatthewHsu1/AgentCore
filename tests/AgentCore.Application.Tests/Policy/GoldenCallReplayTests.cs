using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Policy;
using AgentCore.Application.Tests.Fakes;
using Xunit;

namespace AgentCore.Application.Tests.Policy;

/// <summary>
/// Rule 15 of section 11. The six golden calls of <c>spike/callpolicy</c> replay against a
/// configuration-declared policy and reproduce the hand-written machine exactly.
/// </summary>
public sealed class GoldenCallReplayTests
{
    private static readonly AgentCoreConfiguration Document = ConfigurationLoader.LoadYaml(GoldenSet.Yaml);

    public static TheoryData<string> CallNames()
    {
        TheoryData<string> names = [];
        foreach (var call in GoldenSet.Calls)
        {
            names.Add(call.Name);
        }

        return names;
    }

    [Fact]
    public void GoldenSet_HoldsSixCalls() => Assert.Equal(6, GoldenSet.Calls.Count);

    [Theory]
    [MemberData(nameof(CallNames))]
    public void GoldenCall_ReplaysThroughTheDeclaredPolicyExactly(string name)
    {
        var call = GoldenSet.Calls.Single(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));

        var expected = GoldenSet.ReplayHandWritten(call).Select(Transitions.ToStageId).ToList();
        var actual = ReplayDeclared(call);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EveryGoldenCall_ReplaysThroughTheDeclaredPolicyExactly()
    {
        foreach (var call in GoldenSet.Calls)
        {
            var expected = GoldenSet.ReplayHandWritten(call).Select(Transitions.ToStageId).ToList();
            Assert.Equal(expected, ReplayDeclared(call));
        }
    }

    [Fact]
    public void HumanRequest_BeatsAResolvedFix()
    {
        var call = GoldenSet.Calls.Single(candidate =>
            string.Equals(candidate.Name, "human-request-beats-a-resolved-fix", StringComparison.Ordinal));

        // Both resolved and callerAskedForHuman are true on the last turn, and the answer is escalate.
        Assert.Equal("escalate", ReplayDeclared(call)[^1]);
    }

    [Fact]
    public void DeclaredPolicy_RendersEachGuardNameNextToItsEdge()
    {
        StagePolicy policy = new(Document.Policy!, new TestGuardEvaluator(Document));

        var mermaid = policy.ToMermaid();

        // Section 8.4: a name renders, and an inline JSONLogic tree renders as an unreadable blob.
        Assert.Contains("wantsHuman", mermaid, StringComparison.Ordinal);
        Assert.Contains("humanOrExhausted", mermaid, StringComparison.Ordinal);
        Assert.DoesNotContain("callerSaidGoodbye", mermaid, StringComparison.Ordinal);
    }

    private static List<string> ReplayDeclared(GoldenCall call)
    {
        StagePolicy policy = new(Document.Policy!, new TestGuardEvaluator(Document));

        List<string> stages = new(call.Turns.Count);
        for (var index = 0; index < call.Turns.Count; index++)
        {
            var snapshot = call.Turns[index].Facts.ToSnapshot(policy.Stage, index);
            stages.Add(policy.Advance(snapshot));
        }

        return stages;
    }
}
