using System.Text.Json;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Runtime;
using AgentCore.Application.State;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Transcript;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Runtime;

/// <summary>
/// <c>CallSessionFactory.CreateExtractor</c> is the third site that must strip a
/// <see cref="RenderContent"/> before a model reads it — <c>CallSession.ExtractAsync</c> hands the
/// extractor <c>[turn.Spoken, .. response.Messages]</c>, and <c>response.Messages</c> is exactly the
/// list a drawing tool attaches to. This proves the extractor <see cref="CallSessionFactory"/> builds,
/// not a hand-constructed <c>ModelFacingChatClient</c>.
/// </summary>
public sealed class CallSessionFactoryModelFacingTests
{
    private const string Yaml =
        """
        apiVersion: agentcore/v1
        name: extractor-model-facing-check
        state:
          callerSaidGoodbye: { type: boolean, default: false, writer: extractor }
        extractor:
          model: { ref: fill }
          when: after_reply
        agents:
          items:
            - { id: only }
        """;

    [Fact]
    public async Task TheExtractorTheFactoryBuilds_NeverForwardsARenderContentTheTurnAttached()
    {
        var document = ConfigurationLoader.LoadYaml(Yaml);
        var compiled = ConfigurationCompiler.Compile(
            document, new AgentCompilationContext(new FakeChatClientFactory(new ScriptedChatClient("ok"))));

        RequestCapturingChatClient recorder = new(
            new ScriptedChatClient("""{ "callerSaidGoodbye": null }"""));

        var extractor = CallSessionFactory.CreateExtractor(
            compiled, new RoutingChatClientFactory(new ScriptedChatClient("ok")).Route("fill", recorder));

        Assert.NotNull(extractor);

        var payload = JsonDocument.Parse("""{"x":1}""").RootElement.Clone();
        var drew = new ChatMessage(ChatRole.Assistant,
        [
            new TextContent("here's the order."),
            new RenderContent { Name = "order-card", RenderId = "order-41", Data = payload },
        ]);

        await extractor!.ExtractAsync(
            new StateDocument(compiled.Configuration),
            [new ChatMessage(ChatRole.User, "show me the order"), drew],
            TestContext.Current.CancellationToken);

        var forwarded = Assert.Single(recorder.Requests);
        Assert.DoesNotContain(forwarded, message => message.Contents.Any(content => content is RenderContent));
    }
}
