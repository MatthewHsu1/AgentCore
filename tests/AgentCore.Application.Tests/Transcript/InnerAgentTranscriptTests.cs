using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using Xunit;

namespace AgentCore.Application.Tests.Transcript;

/// <summary>
/// Pins the isolation a <c>kind: agent</c> tool sells: the outer agent buys the inner agent's
/// answer, never its working-out.
/// </summary>
public sealed class InnerAgentTranscriptTests
{
    private const string DelegatingYaml = """
        apiVersion: agentcore/v1
        name: delegation-check
        tools:
          - { id: call_helper, kind: agent, agent: helper, description: "ask the helper" }
        policy:
          initial: talk
          stages:
            - id: talk
              agent: main
              terminal: true
        agents:
          items:
            - { id: main, instructions: "answer the caller", tools: [ call_helper ] }
            - { id: helper, instructions: "look things up" }
        """;

    /// <summary>
    /// The inner agent runs on a session of its own, which no <c>BeginCall</c> ever named, so store 1
    /// never sees its rounds. Keeping them would cost tokens on every later turn and would put the
    /// inner agent's working-out into the audit record, which no consumer asked for.
    /// </summary>
    [Fact]
    public async Task ADelegatingTurn_WritesTheOuterRoundsOnly()
    {
        RecordingTranscriptStore store = new();
        using ToolCallingChatClient model = new("done");
        var compiled = ConfigurationCompiler.Compile(
            ConfigurationLoader.LoadYaml(DelegatingYaml),
            new AgentCompilationContext(new FakeChatClientFactory(model)) { TranscriptStore = store });

        var session = new CallSessionFactory(
            compiled, new GuardEvaluator(compiled.Configuration.Guards), extractor: null).Create();

        await foreach (var _ in session.RunTurnStreamingAsync("hi", TestContext.Current.CancellationToken))
        {
        }

        await session.FlushTranscriptAsync();

        Assert.Equal(2, model.Calls);
        Assert.Equal(
            ["user", "assistant", "tool", "assistant"],
            store.Rows.Select(row => row.Content.Role.Value));
        Assert.All(store.Rows, row => Assert.Equal(session.CallId, row.CallId));
    }
}
