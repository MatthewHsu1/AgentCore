using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Conversation;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using AgentCore.Domain;
using AgentCore.Infrastructure.Conversation.Postgres;
using AgentCore.Infrastructure.Tests.Database.Postgres;
using AgentCore.TestSupport;
using Xunit;
using AgentCore.Application.Runtime.Cut;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Infrastructure.Tests.Conversation.Postgres
{
    /// <summary>
    /// A turn's append that PostgreSQL committed, whose answer never reached the session: the session counts it lost,
    /// and must still tell it from another host's write when it next reads the store.
    /// </summary>
    public sealed class PostgresLostAppendTests : PostgresDatabaseTest
    {
        private const string Yaml =
            """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: parent, instructions: "delegate work", model: { ref: parent }, background: [blocker] }
            - { id: blocker, instructions: "work forever", model: { ref: blocker } }
        entries:
          main:
            agent: parent
        """;

        /// <inheritdoc />
        protected override bool Migrated => true;

        [PostgresFact]
        public async Task AnAppendTheStoreCommittedButReportedFailed_OnOneHost_LeavesTheBackgroundTaskRunning()
        {
            // Arrange
            SavesThenFailsStore store = new(new PostgresConversationStore(DataSource));
            BackgroundStatusChatClient parent = new();
            using HangingChildChatClient child = new();
            await using ConversationSession session = Create(store, parent, child);

            _ = await session.RunTurnAsync("go", Token);
            await child.Started.WaitAsync(TimeSpan.FromSeconds(10), Token);
            store.SavesThenFails = true;
            _ = await session.RunTurnAsync("again", Token);
            await session.FlushTranscriptAsync();
            store.SavesThenFails = false;

            // Act
            TurnResult next = await session.RunTurnAsync("status?", Token);

            // Assert
            Assert.Equal(2, next.TurnIndex);
            string status = Assert.Single(parent.Statuses);
            Assert.Contains("Task 1 [Running]", status, StringComparison.Ordinal);
        }

        [PostgresFact]
        public async Task AnAppendTheStoreCommittedButReportedFailed_ThenALateCutThatRemovesItsReply_OnOneHost_LeavesTheBackgroundTaskRunning()
        {
            // Arrange
            SavesThenFailsStore store = new(new PostgresConversationStore(DataSource));
            BackgroundStatusChatClient parent = new();
            using HangingChildChatClient child = new();
            await using ConversationSession session = Create(store, parent, child);

            _ = await session.RunTurnAsync("go", Token);
            await child.Started.WaitAsync(TimeSpan.FromSeconds(10), Token);
            store.SavesThenFails = true;
            _ = await session.RunTurnAsync("again", Token);
            await session.FlushTranscriptAsync();
            store.SavesThenFails = false;

            // A barge-in before any word of turn 1's reply played leaves its reply row empty, so the session deletes it.
            Assert.True(session.Cut(1, new TurnCut(string.Empty, null)));
            await session.FlushTranscriptAsync();

            // Act
            TurnResult next = await session.RunTurnAsync("status?", Token);

            // Assert
            Assert.Equal(2, next.TurnIndex);
            string status = Assert.Single(parent.Statuses);
            Assert.Contains("Task 1 [Running]", status, StringComparison.Ordinal);
        }

        private static ConversationSession Create(IConversationStore store, BackgroundStatusChatClient parent, HangingChildChatClient child)
        {
            RoutingChatClientFactory chatClients = new(parent);
            _ = chatClients.Route("parent", parent);
            _ = chatClients.Route("blocker", child);
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                ConfigurationLoader.LoadYaml(Yaml), new AgentCompilationContext(chatClients) { ConversationStore = store })["main"];

            return new ConversationSessionFactory(compiled, new GuardEvaluator(compiled.Configuration.Guards)).Create();
        }

        /// <summary>A store whose appends, while <see cref="SavesThenFails"/> is set, commit and then fail.</summary>
        private sealed class SavesThenFailsStore(IConversationStore inner) : DelegatingConversationStore(inner)
        {
            public bool SavesThenFails { get; set; }

            public override async ValueTask<IReadOnlyList<ConversationMessage>> AppendAsync(
                string conversationId, IReadOnlyList<ConversationMessageDraft> messages, ConversationSessionState? state = null, CancellationToken cancellationToken = default)
            {
                IReadOnlyList<ConversationMessage> rows = await base.AppendAsync(conversationId, messages, state, cancellationToken);
                return SavesThenFails ? throw new TimeoutException("the answer to a committed append was lost.") : rows;
            }
        }
    }
}
