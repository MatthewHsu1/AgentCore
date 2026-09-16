using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Knowledge.Fakes;
using Microsoft.Agents.AI;
using Xunit;

namespace AgentCore.Application.Tests.Configuration.Compilation;

/// <summary>
/// The five rows of the section 8.2 compile table. The compiler is a table, not a heuristic.
/// </summary>
public sealed class CompileTableTests
{
    internal const string OneAgentYaml =
        """
        apiVersion: agentcore/v1
        agents:
          defaults:
            model: { ref: reply, temperature: 0.3 }
            instructions: |
              the stable cached prefix
          items:
            - { id: only, instructions: "the stage delta" }
        entries:
          main:
            agent: only
        """;

    private const string PolicyAndGraphYaml =
        """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: first }
            - { id: second }
        entries:
          main:
            policy:
              initial: one
              stages:
                - { id: one, agent: first, to: [ { stage: two } ] }
                - { id: two, agent: second, terminal: true }
            graph:
              pattern: sequential
              agents: [ first, second ]
        """;

    /// <summary>The <c>agents.defaults.instructions</c> of <see cref="SharedPrefixYaml"/>.</summary>
    private const string SharedPrefix = "the stable cached prefix";

    /// <summary>Two agents, one shared prefix, and a delta on each of them.</summary>
    private const string SharedPrefixYaml =
        """
        apiVersion: agentcore/v1
        agents:
          defaults:
            model: { ref: reply }
            instructions: |
              the stable cached prefix
          items:
            - { id: greeter, instructions: "the greeter delta" }
            - { id: closer, instructions: "the closer delta" }
        entries:
          main:
            graph:
              pattern: sequential
              agents: [ greeter, closer ]
        """;

    private const string PatternYaml =
        """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: first }
            - { id: second }
        entries:
          main:
            graph:
              pattern: sequential
              agents: [ first, second ]
        """;

    internal const string ExplicitGraphYaml =
        """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: first }
            - { id: second }
        entries:
          main:
            graph:
              nodes:
                - { id: start, agent: first, start: true }
                - { id: finish, agent: second, output: true }
              edges:
                - { from: start, to: finish }
        """;
    [Fact]
    public void Row1_OneAgentAndNoRuntime_IsTheSingleAgentShape()
    {
        var document = ConfigurationLoader.LoadYaml(OneAgentYaml);
        Assert.Equal(CompiledAgentShape.SingleAgent, ConfigurationCompiler.SelectEntryRow(document.Entries["main"], "/entries/main").Shape);
    }

    [Fact]
    public void Row1_CompilesToAChatClientAgentInstrumentedForOpenTelemetry()
    {
        var compiled = Compile(OneAgentYaml);

        // Task 6a wraps every compiled agent for OpenTelemetry exactly once (ConfigurationCompiler's
        // Resolve, the single place agents are built and cached). compiled.Agent is therefore the
        // OpenTelemetryAgent, not the ChatClientAgent underneath it — GetService<T> is how a caller,
        // and this test, reaches through a DelegatingAIAgent to what it wraps.
        Assert.IsType<OpenTelemetryAgent>(compiled.Agent);
        Assert.IsType<ChatClientAgent>(compiled.Agent.GetService<ChatClientAgent>());
        Assert.Equal("only", compiled.Agent.Name);
    }

    [Fact]
    public void Row2_AgentsPlusPolicy_IsThePolicyShape()
    {
        var document = ConfigurationLoader.LoadYaml(Tests.Configuration.ExampleDocument.Yaml);
        Assert.Equal(CompiledAgentShape.Policy, ConfigurationCompiler.SelectEntryRow(document.Entries["phone"], "/entries/phone").Shape);
    }

    [Fact]
    public void Row2_TheSection81Example_Compiles()
    {
        var compiled = Compile(Tests.Configuration.ExampleDocument.Yaml, "phone");

        Assert.Equal(CompiledAgentShape.Policy, compiled.Shape);
        // 5 agents on the stage machine, plus analyst and webchat, which policy: never reaches.
        Assert.Equal(7, compiled.Agents.Count);

        // The initial stage is greeting, and greeting names greeter.
        Assert.Equal("greeter", compiled.Agent.Name);
        Assert.Equal("resolver", compiled.ForStage("resolve")!.Name);
        Assert.Equal("closer", compiled.ForStage("close")!.Name);
    }

    [Fact]
    public void Row2_KnowsEveryStageOfTheExample()
    {
        var compiled = Compile(Tests.Configuration.ExampleDocument.Yaml, "phone");

        Assert.Throws<KeyNotFoundException>(() => compiled.ForStage("nowhere"));
    }

    [Fact]
    public void Row3_GraphWithAPattern_IsThePatternGraphShape()
    {
        var document = ConfigurationLoader.LoadYaml(PatternYaml);
        Assert.Equal(CompiledAgentShape.PatternGraph, ConfigurationCompiler.SelectEntryRow(document.Entries["main"], "/entries/main").Shape);
    }

    [Theory]
    [InlineData("sequential")]
    [InlineData("concurrent")]
    [InlineData("handoff")]
    [InlineData("group_chat")]
    public void Row3_EveryPattern_CompilesThroughAgentWorkflowBuilder(string pattern)
    {
        var yaml = PatternYaml.Replace("pattern: sequential", "pattern: " + pattern, StringComparison.Ordinal);

        var compiled = Compile(yaml);

        Assert.Equal(CompiledAgentShape.PatternGraph, compiled.Shape);
        Assert.Equal("main", compiled.Agent.Name);
    }

    [Fact]
    public void Row4_GraphWithNodesAndEdges_IsTheExplicitGraphShape()
    {
        var document = ConfigurationLoader.LoadYaml(ExplicitGraphYaml);
        Assert.Equal(CompiledAgentShape.ExplicitGraph, ConfigurationCompiler.SelectEntryRow(document.Entries["main"], "/entries/main").Shape);
    }

    [Fact]
    public void Row4_CompilesThroughWorkflowBuilderAndAsAIAgent()
    {
        var compiled = Compile(ExplicitGraphYaml);

        Assert.Equal(CompiledAgentShape.ExplicitGraph, compiled.Shape);
        Assert.Equal("main", compiled.Agent.Name);
        Assert.IsNotType<ChatClientAgent>(compiled.Agent);
    }

    [Fact]
    public void Row4_AGraphWithoutOneStartNode_IsALoadTimeError()
    {
        var yaml = ExplicitGraphYaml.Replace("{ id: start, agent: first, start: true }", "{ id: start, agent: first }", StringComparison.Ordinal);

        var failure = Assert.Throws<ConfigurationLoadException>(() => Compile(yaml));

        Assert.Equal("/entries/main/graph/nodes", failure.Pointer);
        Assert.Contains("start nodes", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Row4_AGuardedEdgeWithNoEvaluator_IsALoadTimeError()
    {
        var yaml = ExplicitGraphYaml.Replace(
            "- { from: start, to: finish }",
            "- { from: start, to: finish, when: always }",
            StringComparison.Ordinal)
            + "\nguards:\n  always: { \"===\": [ 1, 1 ] }\n";

        var failure = Assert.Throws<ConfigurationLoadException>(() => Compile(yaml));

        Assert.Equal("/entries/main/graph/edges/0/when", failure.Pointer);
    }

    [Fact]
    public void Row5_BothPolicyAndGraph_IsRejectedWhenTheDocumentLoads()
    {
        // Check 1 of section 8.5 already refuses the shape, so the document never reaches the table.
        Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml(PolicyAndGraphYaml));
    }

    [Fact]
    public void Row5_BothPolicyAndGraph_IsALoadTimeErrorInTheTableToo()
    {
        // The same rule again, against a record built in code. Row 5 is a rule of the table, and not
        // only a rule of the JSON Schema.
        var document = ConfigurationLoader.LoadYaml(Tests.Configuration.ExampleDocument.Yaml);
        var both = document.Entries["phone"] with
        {
            Graph = new GraphConfiguration
            {
                Pattern = GraphPattern.Sequential,
                Agents = ["greeter", "closer"],
            },
        };

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationCompiler.SelectEntryRow(both, "/entries/phone"));

        Assert.Equal("/entries/phone", failure.Pointer);
        Assert.Contains("two of agent:, policy:, and graph:", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoAgentsAndNoRuntime_IsALoadTimeError()
    {
        var yaml =
            """
            apiVersion: agentcore/v1
            agents:
              items:
                - { id: first }
                - { id: second }
            entries:
              main: {}
            """;

        var failure = Assert.Throws<ConfigurationLoadException>(
            () => ConfigurationLoader.LoadYaml(yaml));

        Assert.Equal("/entries/main", failure.Pointer);
    }

    [Fact]
    public void ADocumentWithNothingToCompile_IsALoadTimeError()
    {
        var yaml =
            """
            apiVersion: agentcore/v1
            agents:
              items:
                - { id: only }
            entries:
              main: {}
            """;

        var failure = Assert.Throws<ConfigurationLoadException>(
            () => ConfigurationLoader.LoadYaml(yaml));
        Assert.Equal("/entries/main", failure.Pointer);
    }

    [Fact]
    public void AStageThatNamesAnUndeclaredAgent_IsALoadTimeError()
    {
        var yaml =
            """
            apiVersion: agentcore/v1
            agents:
              items:
                - { id: first }
            entries:
              main:
                policy:
                  initial: one
                  stages:
                    - { id: one, agent: first, to: [ { stage: two } ] }
                    - { id: two, agent: nobody, terminal: true }
            """;

        var failure = Assert.Throws<ConfigurationLoadException>(() => Compile(yaml));

        Assert.Equal("/entries/main/policy/stages/1/agent", failure.Pointer);
    }

    [Fact]
    public void AnInitialStageThatNamesNoAgent_IsALoadTimeError()
    {
        var yaml =
            """
            apiVersion: agentcore/v1
            agents:
              items:
                - { id: only }
            entries:
              main:
                policy:
                  initial: one
                  stages:
                    - { id: one, terminal: true }
            """;

        var failure = Assert.Throws<ConfigurationLoadException>(() => Compile(yaml));

        Assert.Equal("/entries/main/policy/initial", failure.Pointer);
    }

    [Fact]
    public void TheCachedPrefix_SitsAboveTheStageDelta()
    {
        var document = ConfigurationLoader.LoadYaml(OneAgentYaml);

        var composed = AgentInstructions.Compose(document.Agents!.Defaults, document.Agents.Items[0]);

        Assert.Equal("the stable cached prefix" + AgentInstructions.Separator + "the stage delta", composed);
        Assert.StartsWith("the stable cached prefix", composed, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAgentWithNoDelta_KeepsThePrefixAlone()
    {
        AgentDefaults defaults = new() { Instructions = "prefix" };
        AgentConfiguration agent = new() { Id = "solo" };

        Assert.Equal("prefix", AgentInstructions.Compose(defaults, agent));
        Assert.Equal("delta", AgentInstructions.Compose(null, agent with { Instructions = "delta" }));
        Assert.Null(AgentInstructions.Compose(null, agent));
    }

    [Fact]
    public void EveryCompiledAgent_CarriesTheCachedPrefixAboveItsOwnDelta()
    {
        var compiled = Compile(SharedPrefixYaml);

        var greeter = compiled.Agents["greeter"].GetService<ChatClientAgent>();
        var closer = compiled.Agents["closer"].GetService<ChatClientAgent>();
        Assert.NotNull(greeter);
        Assert.NotNull(closer);

        // Section 8.1 makes agents.defaults.instructions a cached prefix, and the compiler has one
        // build path for every agent. This asserts that path and not AgentInstructions.Compose: an
        // agent the compiler built without the prefix would defeat the cache for every later turn.
        Assert.Equal(SharedPrefix + AgentInstructions.Separator + "the greeter delta", greeter.Instructions);
        Assert.Equal(SharedPrefix + AgentInstructions.Separator + "the closer delta", closer.Instructions);
        Assert.StartsWith(SharedPrefix, greeter.Instructions, StringComparison.Ordinal);
        Assert.StartsWith(SharedPrefix, closer.Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryAgent_TakesTheModelTheDocumentNames()
    {
        var document = ConfigurationLoader.LoadYaml(Tests.Configuration.ExampleDocument.Yaml);
        using ScriptedChatClient client = new("ok");
        FakeChatClientFactory factory = new(client);

        _ = ConfigurationCompiler.CompileAll(
            document,
            new AgentCompilationContext(factory) { Knowledge = new StubKnowledgePort([]) })["phone"];

        // 5 agents on the stage machine, plus analyst and webchat, which policy: never reaches.
        Assert.Equal(7, factory.Requested.Count);
        Assert.All(factory.Requested, model => Assert.Equal("reply", model!.Ref));
    }

    internal static CompiledAgent Compile(string yaml, string entryName = "main")
    {
        var document = ConfigurationLoader.LoadYaml(yaml);
        var client = new ScriptedChatClient("ok");
        return ConfigurationCompiler.CompileAll(
            document,
            new AgentCompilationContext(new FakeChatClientFactory(client)) { Knowledge = new StubKnowledgePort([]) })[entryName];
    }
}
