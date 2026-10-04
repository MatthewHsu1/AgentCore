using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Conversation;
using AgentCore.Application.Hooks;
using AgentCore.Domain.Audit;
using AgentCore.Infrastructure.Audit.Postgres;
using AgentCore.Infrastructure.Conversation.Postgres;
using AgentCore.Infrastructure.Tests.Database.Postgres;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Infrastructure.Tests.Conversation.Postgres
{
    /// <summary>
    /// A real turn's words in the conversation store and its events in the audit store, checked the way the store's verify query checks
    /// them: the words the rows say must hash to what the chain proves, whatever way the turn ended.
    /// </summary>
    public sealed class PostgresTurnVerificationTests : PostgresDatabaseTest
    {
        private const string Yaml =
            """
        apiVersion: agentcore/v1
        tools:
          - { id: price_lookup, kind: builtin, uses: orders.read, description: "Look up the price of an item." }
        agents:
          items:
            - { id: only, instructions: "quote the price", tools: [ price_lookup ] }
        entries:
          main:
            agent: only
        """;

        /// <inheritdoc />
        protected override bool Migrated => true;

        [PostgresFact]
        public async Task AToolThatSpendsItsBudgetAfterTheModelSpoke_VerifiesAgainstTheChain()
        {
            // Arrange
            PostgresConversationStore store = new(DataSource);
            InMemoryAuditSink events = new();
            ConversationSession session = Create(
                store, events, new AnnouncingToolChatClient(answer: null), () => throw new TimeoutException("the tool is down."));

            // Act
            _ = await session.RunTurnAsync("what does it cost", Token);
            TranscriptTurnDigest turn = await VerifyAsync(store, events, session);

            // Assert
            Assert.Equal(
                "Checking 1. Checking 2. Checking 3. Checking 4.I am sorry. I could not finish that. Please say it again.",
                turn.Spoken);
            Assert.Equal("40e79eac3b0c25fe5d27e0fd6e0ee02aecc48ff93b81af341e0ec3cdb875812d", turn.ReplyTextSha256);
        }

        [PostgresFact]
        public async Task AReaderThatLeavesDuringTheSecondStep_VerifiesAgainstTheChain()
        {
            // Arrange
            PostgresConversationStore store = new(DataSource);
            InMemoryAuditSink events = new();
            ConversationSession session = Create(
                store, events, new AnnouncingToolChatClient("the price is fifty"), () => "{ \"price\": 50 }");

            // Act
            await foreach (ChatResponseUpdate update in session.RunTurnStreamingAsync("what does it cost", Token))
            {
                if (update.Text == "the")
                {
                    break;
                }
            }

            TranscriptTurnDigest turn = await VerifyAsync(store, events, session);

            // Assert
            Assert.Equal("Checking 1. the", turn.Spoken);
            Assert.Equal("3ee72d35ecf4692c68e8a81bf13607d2096fb1e58b4eb133c45f40ae583ced68", turn.ReplyTextSha256);
        }

        private static ConversationSession Create(
            PostgresConversationStore store, InMemoryAuditSink events, IChatClient model, Func<string> tool)
        {
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(Yaml);
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                document,
                new AgentCompilationContext(new RoutingChatClientFactory(model))
                {
                    ConversationStore = store,
                    Tools = TestToolRegistry.From(
                        document, declared => AIFunctionFactory.Create(tool, declared.Id, declared.Description ?? declared.Id), Token),
                })["main"];

            return new ConversationSessionFactory(
                compiled,
                new GuardEvaluator(compiled.Configuration.Guards),
                hooks: BuiltInHooks.Create(events)).Create();
        }

        /// <summary>Files the session's events in the audit store and runs the store's verify query over turn 0.</summary>
        private async Task<TranscriptTurnDigest> VerifyAsync(
            PostgresConversationStore store, InMemoryAuditSink events, ConversationSession session)
        {
            await session.FlushTranscriptAsync();
            await session.FlushNoticesAsync();

            // Not disposed: the sink would take the test's own pool with it.
            PostgresAuditSink chain = new(DataSource);
            foreach (AuditEvent raised in events.EventsOf(session.ConversationId))
            {
                await chain.AppendAsync(raised, Token);
            }

            return Assert.Single(await store.ReadSpokenTurnsAsync(session.ConversationId, Token));
        }
    }
}
