using System.Text.Json;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Runtime;
using AgentCore.Application.State;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Transcript;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// <c>ConversationSessionFactory.CreateExtractor</c> is the third site that must strip a
    /// <see cref="RenderContent"/> before a model reads it — <c>ConversationSession.ExtractAsync</c> hands the
    /// extractor <c>[turn.Spoken, .. response.Messages]</c>, and <c>response.Messages</c> is exactly the
    /// list a drawing tool attaches to. This proves the extractor <see cref="ConversationSessionFactory"/> builds,
    /// not a hand-constructed <c>ModelFacingChatClient</c>.
    /// </summary>
    public sealed class ConversationSessionFactoryModelFacingTests
    {
        private const string Yaml =
            """
        apiVersion: agentcore/v1
        state:
          callerSaidGoodbye: { type: boolean, default: false, writer: extractor }
        extractor:
          model: { ref: fill }
          when: after_reply
        agents:
          items:
            - { id: only }
        entries:
          main:
            agent: only
        """;

        [Fact]
        public async Task TheExtractorTheFactoryBuilds_NeverForwardsARenderContentTheTurnAttached()
        {
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(Yaml);
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                document, new AgentCompilationContext(new FakeChatClientFactory(new ScriptedChatClient("ok"))))["main"];

            RequestCapturingChatClient recorder = new(
                new ScriptedChatClient(/*lang=json,strict*/ """{ "callerSaidGoodbye": null }"""));

            StateExtractor? extractor = ConversationSessionFactory.CreateExtractor(
                compiled, new RoutingChatClientFactory(new ScriptedChatClient("ok")).Route("fill", recorder));

            Assert.NotNull(extractor);

            JsonElement payload = JsonDocument.Parse("""{"x":1}""").RootElement.Clone();
            ChatMessage drew = new(ChatRole.Assistant,
            [
                new TextContent("here's the order."),
                new RenderContent { Name = "order-card", RenderId = "order-41", Data = payload },
            ]);

            _ = await extractor!.ExtractAsync(
                new StateDocument(compiled.Configuration),
                [new ChatMessage(ChatRole.User, "show me the order"), drew],
                TestContext.Current.CancellationToken);

            IReadOnlyList<ChatMessage> forwarded = Assert.Single(recorder.Requests);
            Assert.DoesNotContain(forwarded, message => message.Contents.Any(content => content is RenderContent));
        }
    }
}
