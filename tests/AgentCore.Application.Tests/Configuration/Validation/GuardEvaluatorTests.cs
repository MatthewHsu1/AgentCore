using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using Xunit;

namespace AgentCore.Application.Tests.Configuration.Validation;

/// <summary>
/// The guard evaluator of section 8.4, and the run-time failure row of section 8.7.
/// </summary>
public sealed class GuardEvaluatorTests
{
    private static readonly string[] OrderingOperators = [">", ">=", "<", "<="];

    private static readonly string[] NonOrderingOperators = ["===", "!==", "in", "var", "+"];

    private static readonly Dictionary<string, JsonNode?> State = new(StringComparer.Ordinal)
    {
        ["yes"] = JsonValue.Create(true),
        ["no"] = JsonValue.Create(false),
        ["turns"] = JsonValue.Create(3),
        ["one"] = JsonValue.Create(1),
        ["nine"] = JsonValue.Create(9),
        ["status"] = JsonValue.Create("shipped"),
        ["text"] = JsonValue.Create("abc"),
    };

    [Theory]
    [InlineData("""{ "var": "yes" }""", true)]
    [InlineData("""{ "var": "no" }""", false)]
    [InlineData("""{ "missing": [ "yes" ] }""", false)]
    [InlineData("""{ "missing": [ "ghost" ] }""", true)]
    [InlineData("""{ "if": [ { "var": "yes" }, true, false ] }""", true)]
    [InlineData("""{ "===": [ { "var": "turns" }, 3 ] }""", true)]
    [InlineData("""{ "===": [ { "var": "status" }, "shipped" ] }""", true)]
    [InlineData("""{ "!==": [ { "var": "turns" }, 4 ] }""", true)]
    [InlineData("""{ ">": [ { "var": "turns" }, 2 ] }""", true)]
    [InlineData("""{ ">=": [ { "var": "turns" }, 3 ] }""", true)]
    [InlineData("""{ "<": [ { "var": "turns" }, 4 ] }""", true)]
    [InlineData("""{ "<=": [ { "var": "turns" }, 3 ] }""", true)]
    [InlineData("""{ "!": { "var": "no" } }""", true)]
    [InlineData("""{ "!!": [ { "var": "yes" } ] }""", true)]
    [InlineData("""{ "and": [ { "var": "yes" }, { "!": { "var": "no" } } ] }""", true)]
    [InlineData("""{ "or": [ { "var": "no" }, { "var": "yes" } ] }""", true)]
    [InlineData("""{ "in": [ { "var": "status" }, [ "shipped", "held" ] ] }""", true)]
    [InlineData("""{ "in": [ { "var": "status" }, [ "held" ] ] }""", false)]
    [InlineData("""{ ">": [ { "+": [ { "var": "turns" }, 1 ] }, 3 ] }""", true)]
    [InlineData("""{ "===": [ { "-": [ { "var": "turns" }, 1 ] }, 2 ] }""", true)]
    [InlineData("""{ "===": [ { "*": [ { "var": "turns" }, 2 ] }, 6 ] }""", true)]
    [InlineData("""{ "===": [ { "/": [ { "var": "turns" }, 3 ] }, 1 ] }""", true)]
    [InlineData("""{ "===": [ { "min": [ 3, 9 ] }, 3 ] }""", true)]
    [InlineData("""{ ">": [ { "max": [ 3, 9 ] }, 5 ] }""", true)]
    public void EveryAllowedOperator_Evaluates(string rule, bool expected)
    {
        var evaluator = new GuardEvaluator(new Dictionary<string, JsonNode>(StringComparer.Ordinal));

        Assert.Equal(expected, evaluator.Evaluate(JsonNode.Parse(rule)!, State));
    }

    /// <summary>
    /// The same twenty operators, each reading at least one slot through <c>var</c>.
    /// </summary>
    /// <remarks>
    /// A guard reads state, so a literal-only proof proves nothing about the engine a guard meets.
    /// The newer <c>JsonLogic.Apply(JsonNode, JsonNode)</c> engine of package version 6.1.0 returns
    /// <see langword="null"/> for <c>min</c> and for <c>max</c> as soon as one operand is a
    /// <c>var</c> read, so the two rows that name them fail on it and hold on the <c>Rule</c> model.
    /// </remarks>
    /// <param name="rule">The rule text.</param>
    /// <param name="expected">What the rule answers against <see cref="State"/>.</param>
    [Theory]
    [InlineData("""{ "var": "turns" }""", true)]
    [InlineData("""{ "missing": [ "turns" ] }""", false)]
    [InlineData("""{ "if": [ { "var": "yes" }, { "var": "turns" }, 0 ] }""", true)]
    [InlineData("""{ "===": [ { "var": "turns" }, { "var": "turns" } ] }""", true)]
    [InlineData("""{ "!==": [ { "var": "turns" }, { "var": "one" } ] }""", true)]
    [InlineData("""{ ">":  [ { "var": "turns" }, { "var": "one" } ] }""", true)]
    [InlineData("""{ ">=": [ { "var": "turns" }, { "var": "turns" } ] }""", true)]
    [InlineData("""{ "<":  [ { "var": "turns" }, { "var": "nine" } ] }""", true)]
    [InlineData("""{ "<=": [ { "var": "turns" }, { "var": "turns" } ] }""", true)]
    [InlineData("""{ "!":  [ { "var": "no" } ] }""", true)]
    [InlineData("""{ "!!": [ { "var": "turns" } ] }""", true)]
    [InlineData("""{ "and": [ { "var": "yes" }, { "var": "turns" } ] }""", true)]
    [InlineData("""{ "or":  [ { "var": "no" }, { "var": "turns" } ] }""", true)]
    [InlineData("""{ "in":  [ { "var": "status" }, [ "shipped", "held" ] ] }""", true)]
    [InlineData("""{ "===": [ { "+": [ { "var": "turns" }, { "var": "one" } ] }, 4 ] }""", true)]
    [InlineData("""{ "===": [ { "-": [ { "var": "turns" }, { "var": "one" } ] }, 2 ] }""", true)]
    [InlineData("""{ "===": [ { "*": [ { "var": "turns" }, { "var": "nine" } ] }, 27 ] }""", true)]
    [InlineData("""{ "===": [ { "/": [ { "var": "nine" }, { "var": "turns" } ] }, 3 ] }""", true)]
    [InlineData("""{ "===": [ { "min": [ { "var": "turns" }, { "var": "nine" } ] }, 3 ] }""", true)]
    [InlineData("""{ "===": [ { "max": [ { "var": "turns" }, { "var": "nine" } ] }, 9 ] }""", true)]
    public void EveryAllowedOperator_EvaluatesWithAVarOperand(string rule, bool expected)
    {
        var evaluator = new GuardEvaluator(new Dictionary<string, JsonNode>(StringComparer.Ordinal));

        Assert.Equal(expected, evaluator.Evaluate(JsonNode.Parse(rule)!, State));
    }

    [Fact]
    public void MinAndMax_ReadASlotRatherThanAnswerNull()
    {
        // This is the whole point of the engine switch. On the newer engine both rules answer null,
        // so both comparisons are false and a guard built on either operator is false at every state.
        var evaluator = new GuardEvaluator(new Dictionary<string, JsonNode>(StringComparer.Ordinal));

        Assert.True(evaluator.Evaluate(JsonNode.Parse("""{ "===": [ { "min": [ { "var": "turns" }, 9 ] }, 3 ] }""")!, State));
        Assert.True(evaluator.Evaluate(JsonNode.Parse("""{ "===": [ { "max": [ { "var": "turns" }, 9 ] }, 9 ] }""")!, State));

        // The pair proves the rules are not vacuously false, and that they read the slot.
        Assert.False(evaluator.Evaluate(JsonNode.Parse("""{ "===": [ { "min": [ { "var": "turns" }, 9 ] }, 9 ] }""")!, State));
        Assert.False(evaluator.Evaluate(JsonNode.Parse("""{ "===": [ { "max": [ { "var": "turns" }, 9 ] }, 3 ] }""")!, State));
    }

    [Fact]
    public void AGuardThatIsBuiltOnMin_DecidesAStageExit()
    {
        var guards = new Dictionary<string, JsonNode>(StringComparer.Ordinal)
        {
            ["capped"] = JsonNode.Parse("""{ ">=": [ { "min": [ { "var": "turns" }, 5 ] }, 3 ] }""")!,
        };
        var evaluator = new GuardEvaluator(guards);

        Assert.True(evaluator.Evaluate(GuardReference.FromName("capped"), State));
    }

    [Fact]
    public void TheAllowListHoldsExactlyTheTwentyOperatorsOfSectionEightFour()
    {
        Assert.Equal(
            [
                "var", "missing", "if", "===", "!==", ">", ">=", "<", "<=", "!", "!!",
                "and", "or", "in", "+", "-", "*", "/", "min", "max",
            ],
            GuardOperators.Allowed);

        Assert.Equal(
            ["==", "!=", "log", "map", "filter", "reduce", "all", "some", "none", "merge", "cat", "substr"],
            GuardOperators.Rejected);

        Assert.All(GuardOperators.Rejected, name => Assert.False(GuardOperators.IsAllowed(name)));
        Assert.All(GuardOperators.Allowed, name => Assert.False(GuardOperators.IsNamedRejected(name)));
    }

    // ---------------------------------------------------------------------------------------------
    // An operator name is matched exactly. A table that folded case would let 'VAR' through check 4
    // and then fail at run time, where JsonLogic knows no such operator.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void TheOperatorTables_MatchAnOperatorNameCaseSensitively()
    {
        Assert.True(GuardOperators.IsAllowed("var"));
        Assert.False(GuardOperators.IsAllowed("VAR"));
        Assert.False(GuardOperators.IsAllowed("Missing"));

        Assert.True(GuardOperators.IsNamedRejected("log"));
        Assert.False(GuardOperators.IsNamedRejected("LOG"));
        Assert.False(GuardOperators.IsNamedRejected("Filter"));
    }

    [Fact]
    public void TheNumericComparisons_AreTheFourOrderingOperators()
    {
        Assert.All(OrderingOperators, name => Assert.True(GuardOperators.IsNumericComparison(name)));
        Assert.All(NonOrderingOperators, name => Assert.False(GuardOperators.IsNumericComparison(name)));
    }

    [Fact]
    public void TheLooseEqualityRejection_NamesTheReplacement()
    {
        Assert.Equal("===", GuardOperators.ReplacementFor("=="));
        Assert.Equal("!==", GuardOperators.ReplacementFor("!="));
        Assert.Null(GuardOperators.ReplacementFor("map"));
        Assert.Contains("'==='", GuardOperators.DescribeRejection("=="), StringComparison.Ordinal);
    }

    [Fact]
    public void ANamedGuard_ResolvesThroughTheTableTheConstructorTakes()
    {
        var guards = new Dictionary<string, JsonNode>(StringComparer.Ordinal)
        {
            ["saidGoodbye"] = JsonNode.Parse("""{ "var": "yes" }""")!,
        };
        var evaluator = new GuardEvaluator(guards);

        Assert.True(evaluator.Evaluate(GuardReference.FromName("saidGoodbye"), State));
        Assert.True(evaluator.TryGetGuard("saidGoodbye", out _));
    }

    [Fact]
    public void AGuardNameTheTableDoesNotHold_IsFalse()
    {
        var evaluator = new GuardEvaluator(new Dictionary<string, JsonNode>(StringComparer.Ordinal));

        Assert.False(evaluator.Evaluate(GuardReference.FromName("ghost"), State));
        Assert.False(evaluator.TryGetGuard("ghost", out _));
    }

    [Fact]
    public void AnUnconditionalExit_ResolvesToARuleThatIsAlwaysTrue()
    {
        var evaluator = new GuardEvaluator(new Dictionary<string, JsonNode>(StringComparer.Ordinal));

        var rule = evaluator.Resolve(null);

        Assert.NotNull(rule);
        Assert.True(evaluator.Evaluate(rule, State));
    }

    [Fact]
    public void AGuardThatThrowsAtRunTime_IsFalseAndIsReportedOnce()
    {
        var failures = new List<string>();
        var evaluator = new GuardEvaluator(
            new Dictionary<string, JsonNode>(StringComparer.Ordinal),
            (description, _) => failures.Add(description));

        // 'and' is on the allow-list, so check 4 passes the rule. Its argument is not an array, and
        // JsonLogic throws while it reads it. Section 8.7 calls that possible and not a defect.
        var rule = JsonNode.Parse("""{ "and": 5 }""")!;

        Assert.False(evaluator.Evaluate(rule, State));
        Assert.False(evaluator.Evaluate(rule, State));
        Assert.Single(failures);
    }

    [Fact]
    public void AGuardThatWillNotParse_NamesTheGuardAndNeverThrowsOutOfTheConstructor()
    {
        var failures = new List<string>();
        var guards = new Dictionary<string, JsonNode>(StringComparer.Ordinal)
        {
            ["good"] = JsonNode.Parse("""{ "var": "yes" }""")!,

            // 'and' is on the allow-list and its argument is not an array, so the rule does not
            // deserialize. The rule now parses while the constructor runs, and section 8.7 keeps the
            // process alive: the failure names the guard, reports once, and the guard is false.
            ["bad"] = JsonNode.Parse("""{ "and": 5 }""")!,
        };

        var evaluator = new GuardEvaluator(guards, (description, _) => failures.Add(description));

        Assert.Equal(["bad"], failures);
        Assert.False(evaluator.Evaluate(GuardReference.FromName("bad"), State));
        Assert.False(evaluator.Evaluate(GuardReference.FromName("bad"), State));
        Assert.Single(failures);
        Assert.True(evaluator.Evaluate(GuardReference.FromName("good"), State));
    }

    [Fact]
    public void AGuardThatWillNotParse_DoesNotThrowWhenNoHandlerIsGiven()
    {
        var guards = new Dictionary<string, JsonNode>(StringComparer.Ordinal)
        {
            ["bad"] = JsonNode.Parse("""{ "and": 5 }""")!,
        };

        var evaluator = new GuardEvaluator(guards);

        Assert.False(evaluator.Evaluate(GuardReference.FromName("bad"), State));
    }

    [Fact]
    public void AnInlineRule_EvaluatesWithoutTheTable()
    {
        var evaluator = new GuardEvaluator(new Dictionary<string, JsonNode>(StringComparer.Ordinal));
        var guard = GuardReference.FromRule(JsonNode.Parse("""{ "!": { "var": "no" } }""")!);

        Assert.True(evaluator.Evaluate(guard, State));
    }

    [Fact]
    public void TheExampleGuards_EvaluateAgainstTheDeclaredState()
    {
        var configuration = ConfigurationLoader.LoadYaml(ExampleDocument.Yaml);
        var evaluator = new GuardEvaluator(configuration.Guards);

        var state = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            ["callerAskedForHuman"] = JsonValue.Create(false),
            ["callerSaidGoodbye"] = JsonValue.Create(false),
            ["machineIdentified"] = JsonValue.Create(true),
            ["resolved"] = JsonValue.Create(false),
            ["failedResolveTurns"] = JsonValue.Create(0),
            [ReservedStateSlots.Stage] = JsonValue.Create("identify"),
        };

        Assert.True(evaluator.Evaluate(GuardReference.FromName("identified"), state));
        Assert.False(evaluator.Evaluate(GuardReference.FromName("saidGoodbye"), state));
        Assert.False(evaluator.Evaluate(GuardReference.FromName("wantsHuman"), state));
        Assert.False(evaluator.Evaluate(GuardReference.FromName("humanOrExhausted"), state));
    }

    [Fact]
    public void BuildData_ClonesEveryValueSoNoNodeKeepsASecondParent()
    {
        var data = GuardEvaluator.BuildData(State);

        Assert.NotSame(State["yes"], data["yes"]);
        Assert.True(JsonNode.DeepEquals(State["yes"], data["yes"]));
    }
}
