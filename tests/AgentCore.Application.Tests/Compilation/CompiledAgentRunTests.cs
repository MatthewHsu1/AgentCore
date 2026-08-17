using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Tests.Fakes;
using Microsoft.Agents.AI;
using Xunit;

namespace AgentCore.Application.Tests.Compilation;

/// <summary>
/// Rule 13 of section 11. A one-agent configuration compiles to a <c>ChatClientAgent</c> and answers
/// a turn, and a <c>graph:</c> configuration compiles through <c>WorkflowBuilder</c> and
/// <c>AsAIAgent()</c> and yields its first non-empty delta before the model finishes.
/// </summary>
/// <remarks>
/// Every test here uses an offline <see cref="ScriptedChatClient"/>. There is no network call and no
/// API key anywhere in this file.
/// </remarks>
public sealed class CompiledAgentRunTests
{
    private static readonly string[] Fragments =
        ["Hello", " there.", " I", " am", " the", " compiled", " agent.", " Done."];

    [Fact]
    public async Task Rule13_AOneAgentConfiguration_AnswersATurn()
    {
        using ScriptedChatClient client = new(Fragments);
        var compiled = Compile(CompileTableTests.OneAgentYaml, client);

        // Task 6a wraps every compiled agent for OpenTelemetry, so compiled.Agent is now the
        // OpenTelemetryAgent and the ChatClientAgent it built sits underneath it.
        Assert.IsType<OpenTelemetryAgent>(compiled.Agent);
        Assert.IsType<ChatClientAgent>(compiled.Agent.GetService<ChatClientAgent>());

        var reply = await compiled.Agent.RunAsync("hello", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(client.FullText, reply.Text);
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task Rule13_AOneAgentConfiguration_StreamsEachDeltaThrough()
    {
        using ScriptedChatClient client = new(Fragments) { GateAfterFirstFragment = true };
        var compiled = Compile(CompileTableTests.OneAgentYaml, client);

        var first = await FirstNonEmptyDeltaAsync(compiled.Agent, client);

        Assert.Equal("Hello", first);
    }

    [Fact]
    public async Task Rule13_AGraphConfiguration_YieldsItsFirstDeltaBeforeTheModelFinishes()
    {
        using ScriptedChatClient client = new(Fragments) { GateAfterFirstFragment = true };
        var compiled = Compile(CompileTableTests.ExplicitGraphYaml, client);

        Assert.Equal(CompiledAgentShape.ExplicitGraph, compiled.Shape);

        // The model holds every fragment after the first, so a seam that aggregated would never
        // reach this line. The gate opens only after the first delta arrives.
        var first = await FirstNonEmptyDeltaAsync(compiled.Agent, client);

        Assert.Equal("Hello", first);
    }

    [Fact]
    public async Task Rule13_AGraphConfiguration_CarriesTheWholeReplyAfterFilteringOnNonEmptyText()
    {
        using ScriptedChatClient client = new(Fragments);
        var compiled = Compile(CompileTableTests.ExplicitGraphYaml, client);

        List<AgentResponseUpdate> updates = [];
        await foreach (var update in compiled.Agent.RunStreamingAsync(
            "hello", cancellationToken: TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        var text = string.Concat(updates.Where(update => !string.IsNullOrEmpty(update.Text)).Select(update => update.Text));

        // Section 8.6: AsAIAgent() yields more updates than text fragments, and the ones that carry
        // no content match the lifecycle events. The synthesis adapter must filter on non-empty Text.
        Assert.True(updates.Count > Fragments.Length, $"the wrapper yielded {updates.Count} updates for {Fragments.Length} fragments");
        Assert.Contains(updates, update => string.IsNullOrEmpty(update.Text));

        // The graph runs both nodes, so the text is the model reply twice over. Nothing is lost.
        Assert.Equal(client.FullText + client.FullText, text);
    }

    [Fact]
    public async Task Rule13_APatternGraph_AnswersATurn()
    {
        var yaml =
            """
            apiVersion: agentcore/v1
            name: sequential-graph
            agents:
              items:
                - { id: first }
                - { id: second }
            graph:
              pattern: sequential
              agents: [ first, second ]
            """;

        using ScriptedChatClient client = new("done");
        var compiled = Compile(yaml, client);

        var reply = await compiled.Agent.RunAsync("hello", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("done", reply.Text, StringComparison.Ordinal);
    }

    private static async Task<string?> FirstNonEmptyDeltaAsync(AIAgent agent, ScriptedChatClient client)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            timeout.Token, TestContext.Current.CancellationToken);

        string? first = null;
        await foreach (var update in agent.RunStreamingAsync("hello", cancellationToken: linked.Token))
        {
            if (string.IsNullOrEmpty(update.Text))
            {
                continue;
            }

            first = update.Text;

            // The reply reached us, so let the model finish. The order proves the seam streams.
            client.OpenGate();
            break;
        }

        client.OpenGate();
        return first;
    }

    private static CompiledAgent Compile(string yaml, ScriptedChatClient client)
        => ConfigurationCompiler.Compile(
            ConfigurationLoader.LoadYaml(yaml),
            new AgentCompilationContext(new FakeChatClientFactory(client)));
}
