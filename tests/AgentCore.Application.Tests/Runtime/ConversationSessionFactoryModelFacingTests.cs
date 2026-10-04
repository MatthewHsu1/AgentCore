using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.State;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Transcript;
using AgentCore.Domain.Sources;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// <c>ConversationSessionFactory.CreateExtractor</c> is the third site that must strip a
    /// <see cref="SourceContent"/> before a model reads it — <c>ConversationSession.ExtractAsync</c> hands the
    /// extractor <c>[turn.Spoken, .. response.Messages]</c>, and <c>response.Messages</c> is exactly the
    /// list a citing tool attaches to. This proves the extractor <see cref="ConversationSessionFactory"/> builds,
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
        public async Task TheExtractorTheFactoryBuilds_NeverForwardsASourceContentTheTurnAttached()
        {
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(Yaml);
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                document, new AgentCompilationContext(new FakeChatClientFactory(new ScriptedChatClient("ok"))))["main"];

            RequestCapturingChatClient recorder = new(
                new ScriptedChatClient(/*lang=json,strict*/ """{ "callerSaidGoodbye": null }"""));

            StateExtractor? extractor = ConversationSessionFactory.CreateExtractor(
                compiled, new RoutingChatClientFactory(new ScriptedChatClient("ok")).Route("fill", recorder));

            Assert.NotNull(extractor);

            ChatMessage cited = new(ChatRole.Assistant,
            [
                new TextContent("here's the order."),
                new SourceContent
                {
                    Source = new SourceReference { SourceId = "order-41", Kind = SourceKind.Document, Title = "Order #41", Origin = "knowledge" },
                    CallId = "call-1",
                },
            ]);

            _ = await extractor!.ExtractAsync(
                new StateDocument(compiled.Configuration),
                [new ChatMessage(ChatRole.User, "show me the order"), cited],
                TestContext.Current.CancellationToken);

            IReadOnlyList<ChatMessage> forwarded = Assert.Single(recorder.Requests);
            Assert.DoesNotContain(forwarded, message => message.Contents.Any(content => content is SourceContent));
        }
    }
}
