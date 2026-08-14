using System.Text;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using Xunit;

namespace AgentCore.Application.Tests.Configuration.Validation;

/// <summary>
/// Checks 2 to 8 of section 8.5, and rule 14 of section 11.
/// </summary>
/// <remarks>
/// Rule 14: each of the eight checks has a failing-configuration test that asserts the message and
/// the JSON Pointer, and check 5 prints the state that makes two guards overlap. Check 1 is tested
/// in <see cref="ConfigurationSchemaValidatorTests"/>, and the seven others are tested here.
/// </remarks>
public sealed class ConfigurationValidatorTests
{
    [Fact]
    public void TheExample_PassesEveryCheck()
    {
        var configuration = ConfigurationLoader.LoadYaml(ExampleDocument.Yaml);

        var result = ConfigurationValidator.Evaluate(configuration);

        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void TheShippedExampleFile_PassesEveryCheck()
    {
        var path = Path.Combine(RepositoryRoot(), "src", "AgentCore.Api", "config", "example.yaml");
        Assert.True(File.Exists(path), $"The shipped example is missing at '{path}'.");

        var result = ConfigurationValidator.Evaluate(ConfigurationLoader.LoadFile(path));

        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void TheExampleAsJson_PassesEveryCheck()
    {
        var configuration = ConfigurationLoader.LoadJson(ExampleDocument.Json);

        Assert.Empty(ConfigurationValidator.Evaluate(configuration).Errors);
    }

    // ---------------------------------------------------------------------------------------------
    // Check 2: reference resolution.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void AnUnknownAgent_FailsCheckTwoWithThePointerOfTheStage()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            agents:
              items:
                - { id: greeter }
            policy:
              initial: greeting
              stages:
                - { id: greeting, agent: ghost, terminal: true }
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.ReferenceResolution));

        Assert.Equal("/policy/stages/0/agent", error.Pointer);
        Assert.Equal("the agent 'ghost' is not declared in agents.items", error.Message);
    }

    [Fact]
    public void AnUnknownTool_FailsCheckTwoWithThePointerOfTheSlot()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            state:
              orderStatus: { type: string, writer: tool, from: lookup_order.status }
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.ReferenceResolution));

        Assert.Equal("/state/orderStatus/from", error.Pointer);
        Assert.Equal("the tool 'lookup_order' is not declared in tools:", error.Message);
    }

    [Fact]
    public void AnUnknownGuard_FailsCheckTwoWithThePointerOfTheExit()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            agents:
              items:
                - { id: greeter }
            policy:
              initial: greeting
              stages:
                - id: greeting
                  agent: greeter
                  to: [ { stage: close, when: ghostGuard } ]
                - { id: close, agent: greeter, terminal: true }
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.ReferenceResolution));

        Assert.Equal("/policy/stages/0/to/0/when", error.Pointer);
        Assert.Equal("the guard 'ghostGuard' is not declared in guards:", error.Message);
    }

    [Fact]
    public void AnUnknownExtractorModel_FailsCheckTwoWithThePointerOfTheReference()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            extractor:
              model: { ref: ghost }
            providers:
              llm:
                - { kind: openai, model: gpt-4.1-mini, as: reply }
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.ReferenceResolution));

        Assert.Equal("/extractor/model/ref", error.Pointer);
        Assert.Equal("the model 'ghost' is not declared in providers.llm", error.Message);
    }

    [Fact]
    public void AnUnknownJudgeModel_FailsCheckTwoWithThePointerOfTheReference()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            evaluation:
              judge: { ref: ghost }
            providers:
              llm:
                - { kind: openai, model: gpt-4.1-mini, as: reply }
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.ReferenceResolution));

        Assert.Equal("/evaluation/judge/ref", error.Pointer);
        Assert.Equal("the model 'ghost' is not declared in providers.llm", error.Message);
    }

    [Fact]
    public void AnEvaluationSectionWithNoJudge_PassesCheckTwo()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: quiet
            evaluation:
              sampleRate: 0
            providers:
              llm:
                - { kind: openai, model: gpt-4.1-mini, as: reply }
            """;

        Assert.Empty(Evaluate(document, ConfigurationCheck.ReferenceResolution));
    }

    [Fact]
    public void AnUnknownAgentModel_FailsCheckTwoWithThePointerOfThatAgent()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            agents:
              items:
                - { id: greeter, model: { ref: ghost } }
            providers:
              llm:
                - { kind: openai, model: gpt-4.1-mini, as: reply }
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.ReferenceResolution));

        Assert.Equal("/agents/items/0/model/ref", error.Pointer);
        Assert.Equal("the model 'ghost' is not declared in providers.llm", error.Message);
    }

    [Fact]
    public void AnUnknownDefaultModel_FailsCheckTwoWithThePointerOfTheDefaults()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            agents:
              defaults:
                model: { ref: ghost }
              items:
                - { id: greeter }
            providers:
              llm:
                - { kind: openai, model: gpt-4.1-mini, as: reply }
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.ReferenceResolution));

        Assert.Equal("/agents/defaults/model/ref", error.Pointer);
        Assert.Equal("the model 'ghost' is not declared in providers.llm", error.Message);
    }

    [Fact]
    public void AModelReferenceWithNoProvidersSection_FailsCheckTwo()
    {
        // An absent providers: declares no model name, so the reference is unknown. Check 2 reads an
        // absent tools: and an absent agents: the same way.
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            extractor:
              model: { ref: fill }
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.ReferenceResolution));

        Assert.Equal("/extractor/model/ref", error.Pointer);
        Assert.Equal("the model 'fill' is not declared in providers.llm", error.Message);
    }

    [Fact]
    public void ADocumentThatNamesNoModel_PassesCheckTwoWithNoProvidersSection()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: fine
            agents:
              items:
                - { id: greeter }
            """;

        Assert.Empty(ConfigurationValidator.Evaluate(ConfigurationLoader.LoadYaml(document)).Errors);
    }

    [Fact]
    public void AnUnusedAgentToolNamingAnUndeclaredAgent_FailsCheckTwo()
    {
        // No agent lists this tool, so only the compiler used to catch it, and only later.
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            tools:
              - { id: call_ghost, kind: agent, agent: ghost }
            agents:
              items:
                - { id: planner }
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.ReferenceResolution));

        Assert.Equal("/tools/0/agent", error.Pointer);
        Assert.Equal("the agent 'ghost' is not declared in agents.items", error.Message);
    }

    // ---------------------------------------------------------------------------------------------
    // Check 3: one writer for each slot.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void AReservedSlotDeclaredInState_FailsCheckThreeWithThePointerOfTheSlot()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            state:
              stage: { type: string, writer: extractor }
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.SlotWriters));

        Assert.Equal("/state/stage", error.Pointer);
        Assert.Equal(
            "the slot has two writers: 'stage' is a reserved read-only slot that is always present, and state: declares it again",
            error.Message);
    }

    [Fact]
    public void ASlotWithASecondWriterField_FailsCheckThreeWithThePointerOfThatField()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            state:
              failedResolveTurns:
                type: integer
                writer: counter
                increment: { var: turnIndex }
                value: 0
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.SlotWriters));

        Assert.Equal("/state/failedResolveTurns/value", error.Pointer);
        Assert.Equal("the slot has two writers: writer: counter owns it, and 'value:' names a second", error.Message);
    }

    // ---------------------------------------------------------------------------------------------
    // Check 4: guard operators and variables.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void LooseEquality_FailsCheckFourAndTheMessageNamesTheReplacement()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            state:
              resolved: { type: boolean, default: false, writer: extractor }
            guards:
              fixedNow: { "==": [ { var: resolved }, true ] }
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.GuardOperators));

        Assert.Equal("/guards/fixedNow", error.Pointer);
        Assert.Equal("the operator '==' is rejected because it is loose equality. Use '===' instead.", error.Message);
    }

    [Theory]
    [InlineData("==")]
    [InlineData("!=")]
    [InlineData("log")]
    [InlineData("map")]
    [InlineData("filter")]
    [InlineData("reduce")]
    [InlineData("all")]
    [InlineData("some")]
    [InlineData("none")]
    [InlineData("merge")]
    [InlineData("cat")]
    [InlineData("substr")]
    public void EveryRejectedOperator_FailsCheckFour(string rejected)
    {
        var document = $$"""
            apiVersion: agentcore/v1
            name: broken
            state:
              resolved: { type: boolean, default: false, writer: extractor }
            guards:
              bad: { "{{rejected}}": [ { var: resolved }, 1 ] }
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.GuardOperators));

        Assert.Equal("/guards/bad", error.Pointer);
        Assert.Equal(GuardOperators.DescribeRejection(rejected), error.Message);
    }

    [Fact]
    public void AVarNamingAnUndeclaredSlot_FailsCheckFour()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            guards:
              ghost: { var: neverDeclared }
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.GuardOperators));

        Assert.Equal("/guards/ghost", error.Pointer);
        Assert.Equal("the rule reads the slot 'neverDeclared', and state: does not declare it", error.Message);
    }

    [Fact]
    public void ANumericComparisonAgainstABooleanSlot_FailsCheckFour()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            state:
              resolved: { type: boolean, default: false, writer: extractor }
            guards:
              tooMany: { ">=": [ { var: resolved }, 3 ] }
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.GuardOperators));

        Assert.Equal("/guards/tooMany", error.Pointer);
        Assert.Equal(
            "the operator '>=' compares the slot 'resolved', and that slot is a boolean rather than a number",
            error.Message);
    }

    [Fact]
    public void TheUnarySugarFormOfDoubleNegation_FailsCheckFourAndTheMessageNamesTheArrayForm()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            state:
              resolved: { type: boolean, default: false, writer: extractor }
            guards:
              isResolved: { "!!": { var: resolved } }
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.GuardOperators));

        Assert.Equal("/guards/isResolved", error.Pointer);
        Assert.Equal(GuardOperators.DoubleNegationSugarRejection, error.Message);
        Assert.Contains("""{"!!": [ x ]}""", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheArrayFormOfDoubleNegation_PassesCheckFour()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: fine
            state:
              resolved: { type: boolean, default: false, writer: extractor }
            guards:
              isResolved: { "!!": [ { var: resolved } ] }
            """;

        Assert.Empty(ConfigurationValidator.Evaluate(ConfigurationLoader.LoadYaml(document)).Errors);
    }

    [Fact]
    public void TheUnarySugarFormOfSingleNegation_PassesCheckFour()
    {
        // JsonLogic reads the sugar for '!' and not for '!!', so only '!!' is rejected.
        const string document = """
            apiVersion: agentcore/v1
            name: fine
            state:
              resolved: { type: boolean, default: false, writer: extractor }
            guards:
              notResolved: { "!": { var: resolved } }
            """;

        Assert.Empty(ConfigurationValidator.Evaluate(ConfigurationLoader.LoadYaml(document)).Errors);
    }

    [Fact]
    public void AReservedSlotReadInARule_PassesCheckFour()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: fine
            guards:
              inResolve: { "===": [ { var: stage }, "resolve" ] }
              longCall:  { ">=": [ { var: callDurationSeconds }, 90 ] }
              lateTurn:  { ">":  [ { var: turnIndex }, 4 ] }
            """;

        Assert.Empty(ConfigurationValidator.Evaluate(ConfigurationLoader.LoadYaml(document)).Errors);
    }

    // ---------------------------------------------------------------------------------------------
    // Check 5: exclusivity and coverage by evaluation.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void TwoSiblingGuardsTrueAtOnce_FailCheckFiveAndTheMessagePrintsTheState()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            state:
              callerAskedForHuman: { type: boolean, default: false, writer: extractor }
              callerSaidGoodbye:   { type: boolean, default: false, writer: extractor }
            guards:
              saidGoodbye: { var: callerSaidGoodbye }
              wantsHuman:  { var: callerAskedForHuman }
            agents:
              items:
                - { id: greeter }
            policy:
              initial: identify
              stages:
                - id: identify
                  agent: greeter
                  to:
                    - { stage: close,    when: saidGoodbye }
                    - { stage: escalate, when: wantsHuman }
                - { id: close,    agent: greeter, terminal: true }
                - { id: escalate, agent: greeter, terminal: true }
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.GuardExclusivity));

        Assert.Equal("/policy/stages/0/to/1/when", error.Pointer);
        Assert.Equal(
            "the guard 'saidGoodbye' and the guard 'wantsHuman' are both true at the same time. "
            + """The state that triggers it is {"callerSaidGoodbye":true,"callerAskedForHuman":true}.""",
            error.Message);
    }

    [Fact]
    public void AnUnconditionalExitBesideAGuardedOne_FailsCheckFive()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            state:
              resolved: { type: boolean, default: false, writer: extractor }
            guards:
              fixedNow: { var: resolved }
            agents:
              items:
                - { id: greeter }
            policy:
              initial: resolve
              stages:
                - id: resolve
                  agent: greeter
                  to:
                    - { stage: close, when: fixedNow }
                    - { stage: escalate }
                - { id: close,    agent: greeter, terminal: true }
                - { id: escalate, agent: greeter, terminal: true }
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.GuardExclusivity));

        Assert.Equal("/policy/stages/0/to/1", error.Pointer);
        Assert.Contains("the unconditional exit to 'escalate'", error.Message, StringComparison.Ordinal);
        Assert.Contains("""{"resolved":true}""", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoGraphEdgesTrueAtOnce_FailCheckFiveOnTheEdge()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            state:
              resolved:  { type: boolean, default: false, writer: extractor }
              escalated: { type: boolean, default: false, writer: extractor }
            guards:
              fixedNow: { var: resolved }
              handedOff: { or: [ { var: escalated }, { var: resolved } ] }
            agents:
              items:
                - { id: worker }
                - { id: closer }
            graph:
              nodes:
                - { id: start, agent: worker, start: true }
                - { id: left,  agent: closer, output: true }
                - { id: right, agent: closer, output: true }
              edges:
                - { from: start, to: left,  when: fixedNow }
                - { from: start, to: right, when: handedOff }
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.GuardExclusivity));

        Assert.Equal("/graph/edges/1/when", error.Pointer);
        Assert.Contains("the guard 'fixedNow' and the guard 'handedOff'", error.Message, StringComparison.Ordinal);
        Assert.Contains("""{"resolved":true,"escalated":false}""", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANumberSlot_IsBucketedAroundEveryThresholdTheSiblingsMention()
    {
        // failedResolveTurns >= 3 and failedResolveTurns < 3 never overlap, so the buckets must not
        // invent a false positive. failedResolveTurns >= 3 and failedResolveTurns > 1 do overlap at 4.
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            state:
              failedResolveTurns: { type: integer, default: 0, writer: counter, increment: { var: turnIndex } }
            guards:
              exhausted: { ">=": [ { var: failedResolveTurns }, 3 ] }
              tried:     { ">":  [ { var: failedResolveTurns }, 1 ] }
            agents:
              items:
                - { id: greeter }
            policy:
              initial: resolve
              stages:
                - id: resolve
                  agent: greeter
                  to:
                    - { stage: close,    when: exhausted }
                    - { stage: escalate, when: tried }
                - { id: close,    agent: greeter, terminal: true }
                - { id: escalate, agent: greeter, terminal: true }
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.GuardExclusivity));

        Assert.Equal("/policy/stages/0/to/1/when", error.Pointer);
        Assert.Contains("""{"failedResolveTurns":3}""", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AStateDomainAboveTheCeiling_WarnsThatCoverageIsPartial()
    {
        var configuration = ConfigurationLoader.LoadYaml(WideDocument(17));

        var result = ConfigurationValidator.Evaluate(configuration);

        Assert.Empty(result.Errors);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal(ConfigurationCheck.GuardExclusivity, warning.Check);
        Assert.Equal("/policy/stages/0/to", warning.Pointer);
        Assert.Equal(
            "the state domain of the stage 'resolve' passes 65536 points, so check 5 sampled 65536 points at random and its coverage is partial",
            warning.Message);
    }

    // ---------------------------------------------------------------------------------------------
    // Check 6: reachability.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void AnUnreachableStage_FailsCheckSixWithThePointerOfThatStage()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            agents:
              items:
                - { id: greeter }
            policy:
              initial: greeting
              stages:
                - { id: greeting, agent: greeter, terminal: true }
                - { id: island,   agent: greeter, terminal: true }
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.Reachability));

        Assert.Equal("/policy/stages/1", error.Pointer);
        Assert.Equal("the stage 'island' is unreachable from the initial stage 'greeting'", error.Message);
    }

    [Fact]
    public void ANonTerminalStageWithNoExit_FailsCheckSix()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            agents:
              items:
                - { id: greeter }
            policy:
              initial: greeting
              stages:
                - { id: greeting, agent: greeter }
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.Reachability));

        Assert.Equal("/policy/stages/0", error.Pointer);
        Assert.Equal("the stage 'greeting' is not terminal and has no exit", error.Message);
    }

    // ---------------------------------------------------------------------------------------------
    // Check 7: graph well-formedness.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void AGraphWithNoStartNode_FailsCheckSeven()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            agents:
              items:
                - { id: worker }
                - { id: closer }
            graph:
              nodes:
                - { id: first,  agent: worker }
                - { id: second, agent: closer, output: true }
              edges:
                - { from: first, to: second }
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.GraphWellFormedness));

        Assert.Equal("/graph/nodes", error.Pointer);
        Assert.Equal("the graph declares 0 start nodes, and check 7 needs exactly one", error.Message);
    }

    [Fact]
    public void AnOrphanNode_FailsCheckSevenWithThePointerOfThatNode()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            agents:
              items:
                - { id: worker }
                - { id: closer }
            graph:
              nodes:
                - { id: first,  agent: worker, start: true }
                - { id: second, agent: closer, output: true }
                - { id: island, agent: closer, output: true }
              edges:
                - { from: first, to: second }
            """;

        // An orphan is also unreachable, so check 6 speaks too. This test reads check 7 alone.
        var error = SingleFor(document, ConfigurationCheck.GraphWellFormedness);

        Assert.Equal("/graph/nodes/2", error.Pointer);
        Assert.Equal("the node 'island' is an orphan: no edge reaches it and no edge leaves it", error.Message);
    }

    [Fact]
    public void APathThatReachesNoOutput_FailsCheckSeven()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            agents:
              items:
                - { id: worker }
                - { id: closer }
            graph:
              nodes:
                - { id: first,  agent: worker, start: true }
                - { id: second, agent: closer, output: true }
                - { id: sink,   agent: closer }
              edges:
                - { from: first,  to: second }
                - { from: second, to: sink }
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.GraphWellFormedness));

        Assert.Equal("/graph/nodes/2", error.Pointer);
        Assert.Equal("no path from the node 'sink' reaches an output node", error.Message);
    }

    [Fact]
    public void AnUnreachableNode_FailsCheckSix()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            agents:
              items:
                - { id: worker }
                - { id: closer }
            graph:
              nodes:
                - { id: first,  agent: worker, start: true }
                - { id: second, agent: closer, output: true }
                - { id: island, agent: closer, output: true }
              edges:
                - { from: first,  to: second }
                - { from: island, to: second }
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.Reachability));

        Assert.Equal("/graph/nodes/2", error.Pointer);
        Assert.Equal("the node 'island' is unreachable from the start node 'first'", error.Message);
    }

    // ---------------------------------------------------------------------------------------------
    // Check 8: delegation cycles.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void AnAgentAsToolLoop_FailsCheckEightWithThePointerOfTheToolThatClosesIt()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            tools:
              - { id: call_writer,  kind: agent, agent: writer }
              - { id: call_planner, kind: agent, agent: planner }
            agents:
              items:
                - { id: planner, tools: [ call_writer ] }
                - { id: writer,  tools: [ call_planner ] }
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.DelegationCycles));

        Assert.Equal("/agents/items/1/tools/0", error.Pointer);
        Assert.Equal(
            "this tool runs the agent 'planner', and that closes the delegation cycle "
            + "planner -> writer -> planner. The call would never return.",
            error.Message);
    }

    [Fact]
    public void AnAgentThatCallsItself_FailsCheckEight()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            tools:
              - { id: call_self, kind: agent, agent: planner }
            agents:
              items:
                - { id: planner, tools: [ call_self ] }
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.DelegationCycles));

        Assert.Equal("/agents/items/0/tools/0", error.Pointer);
        Assert.Contains("planner -> planner", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATwoAgentDelegationChainWithNoLoop_PassesCheckEight()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: fine
            tools:
              - { id: call_writer, kind: agent, agent: writer }
              - { id: call_editor, kind: agent, agent: editor }
            agents:
              items:
                - { id: planner, tools: [ call_writer ] }
                - { id: writer,  tools: [ call_editor ] }
                - { id: editor }
            """;

        Assert.Empty(ConfigurationValidator.Evaluate(ConfigurationLoader.LoadYaml(document)).Errors);
    }

    [Fact]
    public void AToolWhoseUsesValueMatchesAnAgentId_IsNotADelegationEdge()
    {
        // 'uses:' names a built-in and never an agent, so the match is a coincidence. The old check 8
        // read this pair as a cycle. The tool id matches an agent id here as well, and that is a
        // coincidence too.
        const string document = """
            apiVersion: agentcore/v1
            name: fine
            tools:
              - { id: writer,  kind: builtin, uses: planner }
              - { id: planner, kind: binding, binds: writer }
            agents:
              items:
                - { id: planner, tools: [ writer ] }
                - { id: writer,  tools: [ planner ] }
            """;

        Assert.Empty(ConfigurationValidator.Evaluate(ConfigurationLoader.LoadYaml(document)).Errors);
    }

    // ---------------------------------------------------------------------------------------------
    // The entry point.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void Validate_ThrowsOneExceptionThatCarriesEveryError()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            state:
              stage: { type: string, writer: extractor }
            guards:
              ghost: { var: neverDeclared }
            agents:
              items:
                - { id: greeter }
            policy:
              initial: greeting
              stages:
                - { id: greeting, agent: ghost, terminal: true }
            """;

        var configuration = ConfigurationLoader.LoadYaml(document);

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationValidator.Validate(configuration));

        Assert.Equal(3, failure.Errors.Count);
        Assert.Contains(failure.Errors, error => error.Check == ConfigurationCheck.ReferenceResolution);
        Assert.Contains(failure.Errors, error => error.Check == ConfigurationCheck.SlotWriters);
        Assert.Contains(failure.Errors, error => error.Check == ConfigurationCheck.GuardOperators);
    }

    [Fact]
    public void Validate_ReturnsTheWarningsWhenNothingFails()
    {
        var configuration = ConfigurationLoader.LoadYaml(ExampleDocument.Yaml);

        var result = ConfigurationValidator.Validate(configuration);

        Assert.True(result.IsValid);
    }

    /// <summary>Walks up from the test binaries to the directory that holds the solution file.</summary>
    /// <returns>The repository root.</returns>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentCore.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static ConfigurationError SingleFor(string yaml, ConfigurationCheck check)
    {
        var result = ConfigurationValidator.Evaluate(ConfigurationLoader.LoadYaml(yaml));

        return Assert.Single(result.Errors, error => error.Check == check);
    }

    private static EquatableList<ConfigurationError> Evaluate(string yaml, ConfigurationCheck check)
    {
        var configuration = ConfigurationLoader.LoadYaml(yaml);
        var result = ConfigurationValidator.Evaluate(configuration);

        // Every error the document holds must belong to the check under test.
        Assert.All(result.Errors, error => Assert.Equal(check, error.Check));
        return result.Errors;
    }

    /// <summary>Writes a document whose sibling guards name enough boolean slots to pass the ceiling.</summary>
    /// <param name="slots">How many boolean slots to declare. 17 slots give 131,072 points.</param>
    /// <returns>The YAML text.</returns>
    private static string WideDocument(int slots)
    {
        var text = new StringBuilder();
        text.AppendLine("apiVersion: agentcore/v1");
        text.AppendLine("name: wide");
        text.AppendLine("state:");
        for (var index = 0; index < slots; index++)
        {
            text.AppendLine($"  s{index}: {{ type: boolean, default: false, writer: extractor }}");
        }

        text.AppendLine("guards:");
        text.AppendLine("  first: { var: s0 }");
        text.Append("  second: { and: [ { \"!\": { var: s0 } }");
        for (var index = 1; index < slots; index++)
        {
            text.Append($", {{ var: s{index} }}");
        }

        text.AppendLine(" ] }");
        text.AppendLine("agents:");
        text.AppendLine("  items:");
        text.AppendLine("    - { id: greeter }");
        text.AppendLine("policy:");
        text.AppendLine("  initial: resolve");
        text.AppendLine("  stages:");
        text.AppendLine("    - id: resolve");
        text.AppendLine("      agent: greeter");
        text.AppendLine("      to:");
        text.AppendLine("        - { stage: close,    when: first }");
        text.AppendLine("        - { stage: escalate, when: second }");
        text.AppendLine("    - { id: close,    agent: greeter, terminal: true }");
        text.AppendLine("    - { id: escalate, agent: greeter, terminal: true }");
        return text.ToString();
    }
}
