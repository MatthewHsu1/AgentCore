using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.State;
using AgentCore.Application.Tests.Fakes;
using Xunit;

namespace AgentCore.Application.Tests.State;

/// <summary>
/// The four writers of section 8.3. Every slot has exactly one owner.
/// </summary>
public sealed class StateWriterTests
{
    private const string Yaml =
        """
        apiVersion: agentcore/v1
        name: writers
        state:
          resolved:    { type: boolean, default: false, writer: extractor }
          orderStatus: { type: string,  writer: tool, from: lookup_order.status }
          orderTotal:  { type: number,  writer: tool, from: lookup_order.totals.grand }
          orderCount:  { type: integer, default: -1, writer: tool, from: lookup_order.count }
          brand:       { type: string,  writer: const, value: sole }
          failedResolveTurns:
            type: integer
            default: 0
            writer: counter
            increment:
              and:
                - { "===": [ { var: stage }, "resolve" ] }
                - { "!": { var: resolved } }
        tools:
          - { id: lookup_order, kind: builtin, uses: orders.read }
        agents:
          items:
            - { id: only }
        """;

    private static readonly AgentCoreConfiguration Document = ConfigurationLoader.LoadYaml(Yaml);

    [Fact]
    public void ConstWriter_FillsItsSlot()
    {
        StateDocument state = new(Document);

        Assert.Equal(1, ConstStateWriter.Apply(state));

        Assert.Equal("sole", state.Read("brand")!.GetValue<string>());
        Assert.False(state.IsUnfilled("brand"));
    }

    [Fact]
    public void ToolWriter_FillsItsSlotFromTheNamedField()
    {
        StateDocument state = new(Document);
        var result = JsonNode.Parse("""{ "status": "shipped", "count": 4, "totals": { "grand": 129.5 } }""");

        Assert.Equal(3, ToolStateWriter.Apply(state, "lookup_order", result));

        Assert.Equal("shipped", state.Read("orderStatus")!.GetValue<string>());
        Assert.Equal(4, state.Read("orderCount")!.GetValue<long>());
        Assert.Equal(129.5, state.Read("orderTotal")!.GetValue<double>());
    }

    [Fact]
    public void ToolWriter_IgnoresTheResultOfAnotherTool()
    {
        StateDocument state = new(Document);
        var result = JsonNode.Parse("""{ "status": "shipped" }""");

        Assert.Equal(0, ToolStateWriter.Apply(state, "some_other_tool", result));
        Assert.True(state.IsUnfilled("orderStatus"));
    }

    [Fact]
    public void ToolWriter_AFailedCoercionLeavesTheDefault()
    {
        StateDocument state = new(Document);

        // count is an integer slot, and the tool answered with an object. The declared type wins.
        var result = JsonNode.Parse("""{ "status": "shipped", "count": { "nested": 1 } }""");

        Assert.Equal(1, ToolStateWriter.Apply(state, "lookup_order", result));

        Assert.True(state.IsUnfilled("orderCount"));
        Assert.Equal(-1, state.Read("orderCount")!.GetValue<long>());
    }

    [Fact]
    public void ToolWriter_APathThatReachesNothingLeavesTheDefault()
    {
        StateDocument state = new(Document);
        var result = JsonNode.Parse("""{ "other": "value" }""");

        Assert.Equal(0, ToolStateWriter.Apply(state, "lookup_order", result));
        Assert.True(state.IsUnfilled("orderStatus"));
        Assert.Null(state.Read("orderStatus"));
    }

    [Fact]
    public void CounterWriter_IncrementsWheneverItsRuleIsTrue()
    {
        StateDocument state = new(Document) { Stage = "resolve" };
        CounterStateWriter counter = new(new TestGuardEvaluator(Document));

        Assert.Equal(1, counter.Apply(state));
        Assert.Equal(1, state.Read("failedResolveTurns")!.GetValue<long>());

        counter.Apply(state);
        counter.Apply(state);
        Assert.Equal(3, state.Read("failedResolveTurns")!.GetValue<long>());
    }

    [Fact]
    public void CounterWriter_HoldsStillWhenItsRuleIsFalse()
    {
        StateDocument state = new(Document) { Stage = "greeting" };
        CounterStateWriter counter = new(new TestGuardEvaluator(Document));

        Assert.Equal(0, counter.Apply(state));
        Assert.Equal(0, state.Read("failedResolveTurns")!.GetValue<long>());
        Assert.True(state.IsUnfilled("failedResolveTurns"));
    }

    [Fact]
    public void CounterWriter_HoldsStillOnceTheSlotItWatchesIsTrue()
    {
        StateDocument state = new(Document) { Stage = "resolve" };
        CounterStateWriter counter = new(new TestGuardEvaluator(Document));

        counter.Apply(state);
        state.TryWrite("resolved", JsonValue.Create(true));

        Assert.Equal(0, counter.Apply(state));
        Assert.Equal(1, state.Read("failedResolveTurns")!.GetValue<long>());
    }

    [Fact]
    public void TheThreeReservedSlots_AreAlwaysPresentAndReadOnly()
    {
        StateDocument state = new(Document) { Stage = "resolve", TurnIndex = 7, CallDurationSeconds = 42.5 };

        var snapshot = state.Snapshot();

        Assert.Equal("resolve", snapshot[ReservedStateSlots.Stage]!.GetValue<string>());
        Assert.Equal(7, snapshot[ReservedStateSlots.TurnIndex]!.GetValue<int>());
        Assert.Equal(42.5, snapshot[ReservedStateSlots.CallDurationSeconds]!.GetValue<double>());

        foreach (var reserved in ReservedStateSlots.All)
        {
            Assert.Throws<ArgumentException>(() => state.TryWrite(reserved, JsonValue.Create(1)));
        }
    }

    [Fact]
    public void ASnapshot_HoldsEveryDeclaredSlotAndTheThreeReservedOnes()
    {
        StateDocument state = new(Document);

        var snapshot = state.Snapshot();

        Assert.Equal(Document.State.Count + 3, snapshot.Count);
    }

    [Theory]
    [InlineData(StateSlotType.Boolean, "\"true\"", true)]
    [InlineData(StateSlotType.Boolean, "1", false)]
    [InlineData(StateSlotType.Integer, "\"12\"", true)]
    [InlineData(StateSlotType.Integer, "12.0", true)]
    [InlineData(StateSlotType.Integer, "12.5", false)]
    [InlineData(StateSlotType.Integer, "true", false)]
    [InlineData(StateSlotType.Number, "\"1.5\"", true)]
    [InlineData(StateSlotType.String, "12", true)]
    [InlineData(StateSlotType.String, "[1]", false)]
    public void Coercion_FollowsTheDeclaredType(StateSlotType slotType, string json, bool expected)
    {
        var value = JsonNode.Parse(json);

        Assert.Equal(expected, StateValueCoercion.TryCoerce(value, slotType, out _));
    }

    [Fact]
    public void Coercion_RefusesNull()
        => Assert.False(StateValueCoercion.TryCoerce(null, StateSlotType.Boolean, out _));
}
