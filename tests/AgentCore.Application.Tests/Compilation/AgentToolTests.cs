using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Tests.Fakes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Compilation;

/// <summary>
/// The fourth tool kind of section 8.1: <c>kind: agent</c>, which is agent-as-tool.
/// </summary>
/// <remarks>
/// MAF separates handoff from agent-as-tool. <c>graph:</c> with <c>pattern: handoff</c> already
/// carries handoff, so this kind carries the other half: the outer agent calls the inner agent,
/// reads the reply, and keeps control.
/// </remarks>
public sealed class AgentToolTests
{
    private const string DelegatingYaml =
        """
        apiVersion: agentcore/v1
        name: delegating
        tools:
          - id: ask_specialist
            kind: agent
            agent: specialist
            description: Ask the specialist one product question.
            parameters:
              type: object
              properties: { question: { type: string } }
              required: [ question ]
        agents:
          items:
            - { id: front, instructions: "the caller talks to me", tools: [ ask_specialist ] }
            - { id: specialist, instructions: "I answer product questions" }
        policy:
          initial: talk
          stages:
            - { id: talk, agent: front, terminal: true }
        """;

    private const string TwoCallersYaml =
        """
        apiVersion: agentcore/v1
        name: two-callers
        tools:
          - { id: ask_specialist, kind: agent, agent: specialist, description: Ask the specialist. }
        agents:
          items:
            - { id: front,  tools: [ ask_specialist ] }
            - { id: backup, tools: [ ask_specialist ] }
            - { id: specialist }
        policy:
          initial: talk
          stages:
            - { id: talk, agent: front, to: [ { stage: fallback } ] }
            - { id: fallback, agent: backup, terminal: true }
        """;

    private const string MissingAgentYaml =
        """
        apiVersion: agentcore/v1
        name: missing-inner
        tools:
          - { id: ask_nobody, kind: agent, agent: nobody, description: Ask an agent nobody declared. }
        agents:
          items:
            - { id: front, tools: [ ask_nobody ] }
        """;

    private const string CycleYaml =
        """
        apiVersion: agentcore/v1
        name: cycle
        tools:
          - { id: ask_second, kind: agent, agent: second, description: Ask the second agent. }
          - { id: ask_first,  kind: agent, agent: first,  description: Ask the first agent. }
        agents:
          items:
            - { id: first,  tools: [ ask_second ] }
            - { id: second, tools: [ ask_first ] }
        policy:
          initial: talk
          stages:
            - { id: talk, agent: first, terminal: true }
        """;

    // ---------------------------------------------------------------------------------------------
    // Binding.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void AKindAgentTool_BindsItsAgentField()
    {
        var document = ConfigurationLoader.LoadYaml(DelegatingYaml);

        var tool = Assert.Single(document.Tools);
        Assert.Equal("ask_specialist", tool.Id);
        Assert.Equal(ToolKind.Agent, tool.Kind);
        Assert.Equal("specialist", tool.Agent);
        Assert.Equal("Ask the specialist one product question.", tool.Description);
        Assert.NotNull(tool.Parameters);
    }

    [Fact]
    public void AKindAgentTool_WithNoAgentField_FailsCheckOne()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            tools:
              - { id: ask_specialist, kind: agent, description: Ask the specialist. }
            """;

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml(document));

        Assert.Equal(ConfigurationCheck.DocumentSchema, failure.Check);
        Assert.Contains(failure.Errors, error => error.Pointer.StartsWith("/tools/0", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------------
    // Compiling.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task AKindAgentTool_ReachesTheModelWithTheDeclaredShape()
    {
        using ToolCallingChatClient client = new("the specialist answer");
        var compiled = Compile(DelegatingYaml, client);

        await compiled.Agent.RunAsync("help me", cancellationToken: TestContext.Current.CancellationToken);

        var function = client.Offered[0];

        // The calling model reads the name, the description, and the parameters, and all three come
        // from the document rather than from the framework default.
        Assert.Equal("ask_specialist", function.Name);
        Assert.Equal("Ask the specialist one product question.", function.Description);
        Assert.Contains("question", function.JsonSchema.ToString(), StringComparison.Ordinal);

        // Three requests: the outer agent calls the tool, the inner agent answers, and the outer
        // agent replies. Only the two outer requests carry a tool, because the document gives the
        // inner agent none. The inner tool-calling loop is the inner agent's own.
        Assert.Equal(3, client.Calls);
        Assert.Equal(2, client.Offered.Count);
        Assert.All(client.Offered, offered => Assert.Equal("ask_specialist", offered.Name));
    }

    [Fact]
    public void AKindAgentTool_NeedsNoToolFactory()
    {
        using ToolCallingChatClient client = new("the specialist answer");

        // Every other kind needs AgentCompilationContext.Tools. This one does not, because the inner
        // agent is already in the document. Section 7: section 8 adds no port.
        var compiled = Compile(DelegatingYaml, client);

        Assert.Equal(2, compiled.Agents.Count);
    }

    [Fact]
    public async Task TheOuterAgent_CallsTheInnerAgentAndKeepsControl()
    {
        using ToolCallingChatClient client = new(
            "the specialist answer",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["question"] = "where is my order" });

        var compiled = Compile(DelegatingYaml, client);

        var reply = await compiled.Agent.RunAsync(
            "help me",
            cancellationToken: TestContext.Current.CancellationToken);

        // The outer agent asked for the tool the document declares.
        Assert.Equal(["ask_specialist"], client.Called);

        // The inner agent read the arguments the model filled, as one JSON object.
        Assert.Contains(client.Prompts, prompt => prompt.Contains("where is my order", StringComparison.Ordinal));

        // The inner reply came back as a tool result, and the outer agent produced the final text.
        Assert.Contains("the specialist answer", client.ToolResults);
        Assert.Equal("the specialist answer", reply.Text);
    }

    // ---------------------------------------------------------------------------------------------
    // T44 and rule 16: the compiled agent is a process singleton, and delegation does not break it.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void Rule16_ADelegatingDocument_BuildsTheInnerAgentOnce()
    {
        using ToolCallingChatClient client = new("the specialist answer");
        FakeChatClientFactory factory = new(client);
        var document = ConfigurationLoader.LoadYaml(TwoCallersYaml);

        var compiled = ConfigurationCompiler.Compile(document, new AgentCompilationContext(factory));

        // Three declared agents, so three model resolutions. Two callers share the one specialist,
        // and neither of them compiled a second copy of it. A fourth resolution would prove the
        // opposite, because every ChatClientAgent this compiler builds asks the factory once.
        Assert.Equal(3, compiled.Agents.Count);
        Assert.Equal(3, factory.Requested.Count);
    }

    [Fact]
    public void Rule16_ADelegatingDocument_CompilesOnceThroughTheRegistry()
    {
        using ToolCallingChatClient client = new("the specialist answer");
        FakeChatClientFactory factory = new(client);
        var document = ConfigurationLoader.LoadYaml(TwoCallersYaml);
        AgentCompilationContext context = new(factory);
        CompiledAgentRegistry registry = new();

        var first = registry.GetOrCompile(document, context);
        var second = registry.GetOrCompile(document, context);

        Assert.Same(first, second);
        Assert.Equal(1, registry.CompileCount);
        Assert.Equal(3, factory.Requested.Count);
    }

    [Fact]
    public async Task Rule16_TwentySixSimultaneousCalls_ShareOneDelegatingAgent()
    {
        const int fanOut = 26;

        using ToolCallingChatClient client = new("the specialist answer");
        var document = ConfigurationLoader.LoadYaml(DelegatingYaml);
        AgentCompilationContext context = new(new FakeChatClientFactory(client));
        CompiledAgentRegistry registry = new();

        var token = TestContext.Current.CancellationToken;
        using Barrier gate = new(fanOut);

        var runs = Enumerable.Range(0, fanOut).Select(index => Task.Run(
            async () =>
            {
                var compiled = registry.GetOrCompile(document, context);
                gate.SignalAndWait(token);

                var reply = await compiled.Agent.RunAsync($"call {index}", cancellationToken: token)
                    .ConfigureAwait(false);
                return (compiled, reply.Text);
            },
            token));

        var results = await Task.WhenAll(runs);

        Assert.Equal(1, registry.CompileCount);
        Assert.All(results, result => Assert.Same(results[0].compiled, result.compiled));
        Assert.All(results, result => Assert.Equal("the specialist answer", result.Text));
    }

    // ---------------------------------------------------------------------------------------------
    // Failing at load.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void AKindAgentTool_NamingAnAgentThatDoesNotExist_FailsAtLoad()
    {
        // Check 1 passes: the shape is right, and JSON Schema cannot cross-reference.
        var document = ConfigurationLoader.LoadYaml(MissingAgentYaml);

        using ToolCallingChatClient client = new("never reached");
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => ConfigurationCompiler.Compile(document, new AgentCompilationContext(new FakeChatClientFactory(client))));

        Assert.Equal("/agents/items/0/tools/0", failure.Pointer);
        Assert.Contains("nobody", failure.Message, StringComparison.Ordinal);
        Assert.Contains("agents.items does not declare", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADelegationCycle_FailsAtLoadAndNeverRecursesForever()
    {
        var document = ConfigurationLoader.LoadYaml(CycleYaml);

        using ToolCallingChatClient client = new("never reached");
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => ConfigurationCompiler.Compile(document, new AgentCompilationContext(new FakeChatClientFactory(client))));

        Assert.Contains("first -> second -> first", failure.Message, StringComparison.Ordinal);
    }

    private static CompiledAgent Compile(string yaml, IChatClient client)
    {
        var document = ConfigurationLoader.LoadYaml(yaml);
        return ConfigurationCompiler.Compile(document, new AgentCompilationContext(new FakeChatClientFactory(client)));
    }
}
