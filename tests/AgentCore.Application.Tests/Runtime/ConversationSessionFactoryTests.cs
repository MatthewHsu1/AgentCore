using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Runtime;
using AgentCore.Application.State;
using AgentCore.Application.Tests.Fakes;
using AgentCore.TestSupport;
using Xunit;
using static AgentCore.Application.Tests.Runtime.ConversationSessionTestSupport;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>The factory: conversation ids, per-conversation state, and the extractor it builds.</summary>
    public sealed class ConversationSessionFactoryTests
    {
        // The factory.
        [Fact]
        public void TheFactory_MakesAConversationIdWhenTheHostGivesNone()
        {
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(StayingNull);
            ConversationSessionFactory factory = Build(PolicyYaml, reply, fill);

            ConversationSession first = factory.Create();
            ConversationSession second = factory.Create();

            Assert.NotEmpty(first.ConversationId);
            Assert.NotEqual(first.ConversationId, second.ConversationId);
        }

        [Fact]
        public async Task TheFactory_GivesEachConversationItsOwnStateAndItsOwnMachine()
        {
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(SaidGoodbye, StayingNull);
            ConversationSessionFactory factory = Build(PolicyYaml, reply, fill);

            ConversationSession first = factory.Create("conversation-1");
            ConversationSession second = factory.Create("conversation-2");

            _ = await first.RunTurnAsync("goodbye", TestContext.Current.CancellationToken);

            // One machine belongs to one conversation. The first conversation ended, and the second one has not started.
            Assert.True(first.IsComplete);
            Assert.False(second.IsComplete);
            Assert.Equal("greeting", second.Stage);
            Assert.Empty(second.Transcript);
            Assert.NotSame(first.State, second.State);

            // Both conversations share the one compiled agent. T44 and rule 16.
            Assert.Same(first.Compiled, second.Compiled);
        }

        [Fact]
        public void TheFactory_BuildsNoExtractorWhenTheDocumentDeclaresNone()
        {
            using SequencedChatClient reply = new("hello there.");
            CompiledAgent compiled = Compile(TwoStagesYaml, reply, null, null);

            Assert.Null(ConversationSessionFactory.CreateExtractor(compiled, new FakeChatClientFactory(reply)));
        }

        [Fact]
        public void TheFactory_BuildsTheExtractorTheDocumentDeclares()
        {
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(StayingNull);
            CompiledAgent compiled = Compile(PolicyYaml, reply, fill, null);

            StateExtractor? extractor = ConversationSessionFactory.CreateExtractor(
                compiled,
                new RoutingChatClientFactory(reply).Route("fill", fill));

            Assert.NotNull(extractor);
            Assert.Equal(["callerSaidGoodbye"], extractor.SlotNames);
        }
    }
}
