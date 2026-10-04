using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.Tests.Fakes;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Transcript
{
    /// <summary>A conversation's row exists before its first word does.</summary>
    public sealed class ConversationSessionConversationRowTests
    {
        private const string OneAgentYaml = """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "ok" }
        entries:
          main:
            agent: only
        """;

        /// <summary>A conversation store that is down: it takes no row, so no word may follow.</summary>
        private sealed class RefusingCreate(IConversationStore inner) : DelegatingConversationStore(inner)
        {
            public override ValueTask<ConversationRecord> CreateAsync(
                string conversationId, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("the conversation store is down.");
            }
        }

        [Fact]
        public async Task ATurn_CreatesTheConversationRow_BeforeItWritesAnyWord()
        {
            InMemoryConversationStore store = new();
            using ScriptedChatClient reply = new("hello");
            ConversationSession session = CreateSession(OneAgentYaml, reply, store);

            _ = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            await session.FlushTranscriptAsync();
            Assert.NotNull(await store.GetAsync(session.ConversationId, TestContext.Current.CancellationToken));
            Assert.NotEmpty(await store.ReadAllAsync(session.ConversationId, TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task ATurn_WhenStoreZeroRefusesTheRow_FailsAndWritesNoWords()
        {
            InMemoryConversationStore inner = new();
            RefusingCreate store = new(inner);
            using ScriptedChatClient reply = new("hello");
            ConversationSession session = CreateSession(OneAgentYaml, reply, store);

            InvalidOperationException fault = await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.RunTurnAsync("hi", TestContext.Current.CancellationToken));

            Assert.Equal("the conversation store is down.", fault.Message);
            Assert.Empty(await inner.ReadAllAsync(session.ConversationId, TestContext.Current.CancellationToken));
        }

        private static ConversationSession CreateSession(string yaml, IChatClient reply, IConversationStore store)
        {
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(yaml);
            FakeChatClientFactory chatClients = new(reply);
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                document,
                new AgentCompilationContext(chatClients)
                {
                    ConversationStore = store,
                    Tools = TestToolRegistry.From(document, null, TestContext.Current.CancellationToken),
                })["main"];

            ConversationSessionFactory factory = new(
                compiled,
                new GuardEvaluator(compiled.Configuration.Guards),
                extractor: null);

            return factory.Create();
        }
    }
}
