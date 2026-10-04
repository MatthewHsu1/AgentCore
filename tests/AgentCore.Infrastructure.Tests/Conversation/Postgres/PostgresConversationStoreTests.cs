using System.Text.Json;
using AgentCore.Application.Conversation;
using AgentCore.Infrastructure.Conversation.Postgres;
using AgentCore.Infrastructure.Tests.Database.Postgres;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Conversation.Postgres
{
    /// <summary>The conversation store, in PostgreSQL.</summary>
    public sealed class PostgresConversationStoreTests : PostgresDatabaseTest
    {
        /// <inheritdoc />
        protected override bool Migrated => true;

        [PostgresFact]
        public async Task CreateAsync_ANewConversation_WritesOneRow()
        {
            // Arrange
            PostgresConversationStore store = new(DataSource);

            // Act
            ConversationRecord conversation = await store.CreateAsync("c1", Token);

            // Assert
            Assert.Equal("c1", conversation.ConversationId);
            Assert.Null(conversation.Title);
            Assert.Equal(ConversationStatus.Regular, conversation.Status);
            Assert.Equal(1, await ScalarAsync<long>("SELECT count(*) FROM agentcore.conversation"));
        }

        [PostgresFact]
        public async Task CreateAsync_TheSameIdTwice_StaysOneRowAndKeepsTheFirst()
        {
            // Arrange
            PostgresConversationStore store = new(DataSource);

            // Act
            ConversationRecord first = await store.CreateAsync("c1", Token);
            await store.RenameAsync("c1", "kept", Token);
            ConversationRecord second = await store.CreateAsync("c1", Token);

            // Assert
            Assert.Equal(first.CreatedAt, second.CreatedAt);
            Assert.Equal("kept", second.Title);
            Assert.Equal(1, await ScalarAsync<long>("SELECT count(*) FROM agentcore.conversation"));
        }

        [PostgresFact]
        public async Task GetAsync_AConversationThatWasNeverMade_IsNull()
        {
            // Arrange
            PostgresConversationStore store = new(DataSource);

            // Act
            ConversationRecord? found = await store.GetAsync("missing", Token);

            // Assert
            Assert.Null(found);
        }

        [PostgresFact]
        public async Task GetAsync_AConversationWithNoMessages_ReportsNoLastActivity()
        {
            // Arrange
            PostgresConversationStore store = new(DataSource);
            _ = await store.CreateAsync("c1", Token);

            // Act
            ConversationRecord? found = await store.GetAsync("c1", Token);

            // Assert
            Assert.Null(found!.LastMessageAt);
        }

        [PostgresFact]
        public async Task GetAsync_AConversationWithMessages_ReportsTheNewestMessageTime()
        {
            // Arrange
            PostgresConversationStore store = new(DataSource);
            _ = await store.CreateAsync("c1", Token);
            await ExecuteAsync(
                """
            INSERT INTO agentcore.conversation_message (conversation_id, ordinal, turn_index, role, content, message_id, created_at, updated_at)
            VALUES ('c1', 1, 0, 'user', '{}'::jsonb, 'm1', now(), now() + interval '1 hour')
            """);

            // Act
            ConversationRecord? found = await store.GetAsync("c1", Token);

            // Assert
            Assert.True(found!.LastMessageAt > found.CreatedAt);
        }

        [PostgresFact]
        public async Task RenameAsync_AConversation_ChangesOnlyItsTitle()
        {
            // Arrange
            PostgresConversationStore store = new(DataSource);
            _ = await store.CreateAsync("c1", Token);

            // Act
            await store.RenameAsync("c1", "A squeaky belt", Token);

            // Assert
            ConversationRecord? found = await store.GetAsync("c1", Token);
            Assert.Equal("A squeaky belt", found!.Title);
            Assert.Equal(ConversationStatus.Regular, found.Status);
        }

        [PostgresFact]
        public async Task SetStatusAsync_Archived_IsReadBack()
        {
            // Arrange
            PostgresConversationStore store = new(DataSource);
            _ = await store.CreateAsync("c1", Token);

            // Act
            await store.SetStatusAsync("c1", ConversationStatus.Archived, Token);

            // Assert
            Assert.Equal(ConversationStatus.Archived, (await store.GetAsync("c1", Token))!.Status);
        }

        [PostgresFact]
        public async Task SetCustomAsync_SomeFields_AreReadBackWhole()
        {
            // Arrange
            PostgresConversationStore store = new(DataSource);
            _ = await store.CreateAsync("c1", Token);
            using JsonDocument document = JsonDocument.Parse("""{"crmId":"A-1","tags":["belt"]}""");

            // Act
            await store.SetCustomAsync("c1", document.RootElement, Token);

            // Assert
            ConversationRecord? found = await store.GetAsync("c1", Token);
            Assert.Equal("A-1", found!.Custom!.Value.GetProperty("crmId").GetString());
        }

        [PostgresFact]
        public async Task SetCustomAsync_Null_ClearsTheFields()
        {
            // Arrange
            PostgresConversationStore store = new(DataSource);
            _ = await store.CreateAsync("c1", Token);
            using JsonDocument document = JsonDocument.Parse("""{"crmId":"A-1"}""");
            await store.SetCustomAsync("c1", document.RootElement, Token);

            // Act
            await store.SetCustomAsync("c1", null, Token);

            // Assert
            Assert.Null((await store.GetAsync("c1", Token))!.Custom);
        }

        [PostgresFact]
        public async Task SetExternalIdAsync_AConsumersOwnId_IsReadBack()
        {
            // Arrange
            PostgresConversationStore store = new(DataSource);
            _ = await store.CreateAsync("c1", Token);

            // Act
            await store.SetExternalIdAsync("c1", "crm-77", Token);

            // Assert
            Assert.Equal("crm-77", (await store.GetAsync("c1", Token))!.ExternalId);
        }

        [PostgresFact]
        public async Task SetExternalIdAsync_Null_ClearsTheId()
        {
            // Arrange
            PostgresConversationStore store = new(DataSource);
            _ = await store.CreateAsync("c1", Token);
            await store.SetExternalIdAsync("c1", "crm-77", Token);

            // Act
            await store.SetExternalIdAsync("c1", null, Token);

            // Assert
            Assert.Null((await store.GetAsync("c1", Token))!.ExternalId);
        }

        [PostgresFact]
        public async Task DeleteAsync_AConversation_LeavesNoRowAndNoClaim()
        {
            // Arrange
            PostgresConversationStore store = new(DataSource);
            _ = await store.CreateAsync("c1", Token);
            await store.AttachPrincipalAsync("c1", "person-a", "caller", Token);

            // Act
            await store.DeleteAsync("c1", Token);

            // Assert
            Assert.Equal(0, await ScalarAsync<long>("SELECT count(*) FROM agentcore.conversation"));
            Assert.Equal(0, await ScalarAsync<long>("SELECT count(*) FROM agentcore.conversation_principal"));
        }

        [PostgresFact]
        public async Task DeleteAsync_AConversation_TakesItsResponseContinuationsWithIt()
        {
            // Arrange — a response id's row is FK'd to the conversation it continues (ON DELETE CASCADE),
            // so deleting the conversation must take it too, or a reused id could resolve a dead conversation.
            PostgresConversationStore store = new(DataSource);
            _ = await store.CreateAsync("c1", Token);
            await store.SaveContinuationAsync("resp_1", "c1", Token);

            // Act
            await store.DeleteAsync("c1", Token);

            // Assert
            Assert.Null(await store.FindContinuationAsync("resp_1", Token));
        }

        [PostgresFact]
        public async Task DeleteAsync_AConversationThatWasNeverMade_IsNotAThrow()
        {
            // Arrange
            PostgresConversationStore store = new(DataSource);

            // Act
            Exception? thrown = await Record.ExceptionAsync(() => store.DeleteAsync("missing", Token).AsTask());

            // Assert
            Assert.Null(thrown);
        }
    }
}
