using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Transcript;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Compilation;

/// <summary>
/// <c>ConfigurationCompiler.WithToolFailureAuditing</c> is one of the two sites, besides
/// <c>ShippedAgentBuilder</c>, that puts <see cref="ModelFacingChatClient"/> into a real pipeline
/// rather than a hand-built one. Deleting its <c>.Use(...)</c> line changes no test in
/// <c>ModelFacingChatClientTests</c> at all, because none of them compile a document. This proves the
/// COMPILED agent, not the client on its own.
/// </summary>
public sealed class ConfigurationCompilerModelFacingTests
{
    private const string Yaml =
        """
        apiVersion: agentcore/v1
        name: model-facing-check
        tools:
          - { id: draw_card, kind: builtin, uses: test.draw, description: "Draw a card for the caller." }
        agents:
          items:
            - { id: only, model: { ref: reply }, instructions: "greet the caller", tools: [ draw_card ] }
        """;

    [Fact]
    public async Task TheSecondRoundOfACompiledAgent_NeverForwardsARenderContentTheFirstRoundsToolAttached()
    {
        using var scope = TurnAmbients.Amend(ambients => ambients with { Renders = new TurnRenders() });

        RequestCapturingChatClient recorder = new(new ToolCallingChatClient("done."));
        var document = ConfigurationLoader.LoadYaml(Yaml);
        var compiled = ConfigurationCompiler.Compile(
            document,
            new AgentCompilationContext(new FakeChatClientFactory(recorder))
            {
                Tools = TestToolRegistry.From(
                    document,
                    static declared => declared.Uses == "test.draw" ? DrawCard() : null,
                    TestContext.Current.CancellationToken),
            });

        await compiled.Agent.RunAsync("draw me a card", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, recorder.Requests.Count);
        Assert.DoesNotContain(
            recorder.Requests[1],
            message => message.Contents.Any(content => content is RenderContent));
    }

    private static AIFunction DrawCard()
        => AIFunctionFactory.Create(
            () =>
            {
                TurnAmbients.Current?.Renders?.Publish("card", "card-1", new { text = "hi" });
                return "drawn.";
            },
            "draw_card",
            "Draw a card for the caller.");
}
