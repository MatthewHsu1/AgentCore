using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Runtime;
using AgentCore.Application.State;
using Xunit;

namespace AgentCore.Application.Tests.Runtime;

/// <summary>
/// The seam that carries the state of one call into the graph every call shares.
/// </summary>
/// <remarks>
/// T44 makes the compiled agent a process singleton, so row 4 of the section 8.2 compile table cannot
/// capture the state of one call. It finds the state on the flow of execution instead, and these tests
/// fix what that means: one flow reads one call, a flow that carries none fails loudly, and the scope
/// closes on every exit.
/// </remarks>
public sealed class CallStateScopeTests
{
    private const string Yaml =
        """
        apiVersion: agentcore/v1
        name: scoped
        state:
          escalate: { type: boolean, writer: extractor, default: false }
        agents:
          items:
            - { id: only }
        """;

    [Fact]
    public void AnOpenScope_AnswersTheStateOfTheCall()
    {
        var state = NewState(escalate: true);

        using (CallStateScope.Enter(state))
        {
            var snapshot = CallStateScope.Snapshot();

            Assert.True(snapshot["escalate"]!.GetValue<bool>());
            Assert.Equal(0, snapshot[ReservedStateSlots.TurnIndex]!.GetValue<int>());
        }
    }

    [Fact]
    public void NoScopeAtAll_ThrowsAndDoesNotAnswerFalse()
    {
        // Section 8.2 refuses the silent graph failure. A guarded edge that quietly became
        // unconditional is that failure, so the source throws rather than answering an empty state.
        var failure = Assert.Throws<InvalidOperationException>(CallStateScope.Snapshot);

        Assert.Equal(CallStateScope.NoScopeMessage, failure.Message);
    }

    [Fact]
    public void AClosedScope_LeavesNothingBehind()
    {
        using (CallStateScope.Enter(NewState(escalate: true)))
        {
            _ = CallStateScope.Snapshot();
        }

        Assert.Throws<InvalidOperationException>(CallStateScope.Snapshot);
    }

    [Fact]
    public void AScopeThatThrows_StillCloses()
    {
        Action turnThatThrows = () =>
        {
            using (CallStateScope.Enter(NewState(escalate: true)))
            {
                throw new OperationCanceledException();
            }
        };

        Assert.Throws<OperationCanceledException>(turnThatThrows);

        Assert.Throws<InvalidOperationException>(CallStateScope.Snapshot);
    }

    [Fact]
    public void AnInnerScope_PutsTheOuterOneBack()
    {
        using (CallStateScope.Enter(NewState(escalate: false)))
        {
            using (CallStateScope.Enter(NewState(escalate: true)))
            {
                Assert.True(CallStateScope.Snapshot()["escalate"]!.GetValue<bool>());
            }

            Assert.False(CallStateScope.Snapshot()["escalate"]!.GetValue<bool>());
        }
    }

    [Fact]
    public void ASecondDispose_ChangesNothing()
    {
        var outer = CallStateScope.Enter(NewState(escalate: false));
        var inner = CallStateScope.Enter(NewState(escalate: true));

        inner.Dispose();
        inner.Dispose();

        // The second dispose must not put the older state back over the scope that now holds.
        Assert.False(CallStateScope.Snapshot()["escalate"]!.GetValue<bool>());
        outer.Dispose();
    }

    [Fact]
    public async Task TheScopeFollowsTheFlowOfExecutionAndNotTheThread()
    {
        using (CallStateScope.Enter(NewState(escalate: true)))
        {
            // The continuation lands on a thread pool thread, and it still reads this call.
            await Task.Yield();

            Assert.True(CallStateScope.Snapshot()["escalate"]!.GetValue<bool>());
        }
    }

    [Fact]
    public async Task TwentySixFlowsAtOnce_EachReadTheirOwnCall()
    {
        const int FanOut = 26;
        var token = TestContext.Current.CancellationToken;
        using Barrier gate = new(FanOut);

        var flows = Enumerable.Range(0, FanOut).Select(index => Task.Run(
            async () =>
            {
                var escalate = index % 2 == 0;
                using (CallStateScope.Enter(NewState(escalate)))
                {
                    gate.SignalAndWait(token);
                    await Task.Yield();
                    return (Expected: escalate, Read: CallStateScope.Snapshot()["escalate"]!.GetValue<bool>());
                }
            },
            token));

        var results = await Task.WhenAll(flows);

        Assert.All(results, result => Assert.Equal(result.Expected, result.Read));
    }

    /// <summary>Builds the state of one call, with the one declared slot already filled.</summary>
    /// <param name="escalate">The value the slot holds.</param>
    /// <returns>The state.</returns>
    private static StateDocument NewState(bool escalate)
    {
        StateDocument state = new(ConfigurationLoader.LoadYaml(Yaml));
        state.TryWrite("escalate", JsonValue.Create(escalate));
        return state;
    }
}
