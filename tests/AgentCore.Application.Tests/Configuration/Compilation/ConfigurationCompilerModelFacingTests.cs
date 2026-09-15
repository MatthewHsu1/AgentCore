using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Runtime;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tools.Binding;
using AgentCore.Application.Transcript;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Configuration.Compilation;

/// <summary>
/// <c>ConfigurationCompiler.WithToolFailureAuditing</c> puts <see cref="ModelFacingChatClient"/>
/// into a real pipeline rather than a hand-built one. Deleting its <c>.Use(...)</c> line changes no test in
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
        TurnRenders renders = new();
        var turn = new TurnInvocation { CallId = "call", TurnIndex = 0, Stage = "", Renders = renders };

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

        var session = await compiled.Agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        await compiled.Agent.RunAsync(
            [new ChatMessage(ChatRole.User, "draw me a card")],
            session,
            turn.RunOptions(),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, recorder.Requests.Count);
        Assert.DoesNotContain(
            recorder.Requests[1],
            message => message.Contents.Any(content => content is RenderContent));
    }

    private static AIFunction DrawCard()
        => AIFunctionFactory.Create(
            (TurnInvocation? turn) =>
            {
                turn!.Renders!.Publish("card", "card-1", new { text = "hi" });
                return "drawn.";
            },
            new AIFunctionFactoryOptions
            {
                Name = "draw_card",
                Description = "Draw a card for the caller.",
                ConfigureParameterBinding = ToolParameterBindings.For,
            });
}
