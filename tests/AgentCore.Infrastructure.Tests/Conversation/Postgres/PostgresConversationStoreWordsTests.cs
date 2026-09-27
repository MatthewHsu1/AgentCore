using System.Text.Json.Nodes;
using AgentCore.Application.Conversation;
using AgentCore.Application.Transcript;
using AgentCore.Infrastructure.Conversation.Postgres;
using AgentCore.Infrastructure.Tests.Database.Postgres;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Conversation.Postgres
{
    /// <summary>
    /// The words half of the store, in PostgreSQL.
    /// </summary>
    public sealed class PostgresConversationStoreWordsTests : PostgresDatabaseTest
    {

        /// <summary>Opens the store with the conversation rows these tests write words against already made.</summary>
        private async Task<PostgresConversationStore> OpenAsync()
        {
            PostgresConversationStore store = new(DataSource);
            _ = await store.CreateAsync("C1", Token);
            _ = await store.CreateAsync("C2", Token);
            return store;
        }

        /// <inheritdoc />
        protected override bool Migrated => true;

        // ---------------------------------------------------------------------------------------------
        // Append.
        // ---------------------------------------------------------------------------------------------
        [PostgresFact]
        public async Task AppendAsync_ATurn_WritesOneRowForEachMessage()
        {
            // Arrange
            PostgresConversationStore store = await OpenAsync();

            // Act
            _ = await store.AppendAsync("C1", Turn(turnIndex: 0, idSeed: 0), cancellationToken: Token);

            // Assert
            Assert.Equal(2L, await ScalarAsync<long>("SELECT count(*) FROM agentcore.conversation_message"));
        }

        [PostgresFact]
        public async Task AppendAsync_ATurn_LiftsTheRoleOutOfTheContent()
        {
            // Arrange
            PostgresConversationStore store = await OpenAsync();

            // Act
            _ = await store.AppendAsync("C1", Turn(turnIndex: 0, idSeed: 0), cancellationToken: Token);

            // Assert — retention and redaction read the role, and never parse the content to find it.
            Assert.Equal(
                "user,assistant",
                await ScalarAsync<string>("SELECT string_agg(role, ',' ORDER BY ordinal) FROM agentcore.conversation_message"));
        }

        [PostgresFact]
        public async Task AppendAsync_AToolCallingTurn_RoundTripsEveryContentPart()
        {
            // Arrange — the framework ships the polymorphic converters, so a tool call and its result
            // survive with no code of ours.
            PostgresConversationStore store = await OpenAsync();
            ChatMessage announced = new(
                ChatRole.Assistant,
                [new TextContent("Let me check that."), new FunctionCallContent("id1", "lookup", null)]);
            ChatMessage result = new(ChatRole.Tool, [new FunctionResultContent("id1", "Friday")]);

            // Act
            _ = await store.AppendAsync(
                "C1",
                [
                    new ConversationMessageDraft(0, announced, "m0"),
                    new ConversationMessageDraft(0, result, "m1"),
                ],
                cancellationToken: Token);

            // Assert
            IReadOnlyList<ConversationMessage> rows = await store.ReadAllAsync("C1", Token);
            Assert.Equal(
                ["Let me check that.", "lookup", "id1"],
                [
                    rows[0].Content.Contents.OfType<TextContent>().Single().Text,
                    rows[0].Content.Contents.OfType<FunctionCallContent>().Single().Name,
                    rows[1].Content.Contents.OfType<FunctionResultContent>().Single().CallId,
                ]);
        }

        [PostgresFact]
        public async Task AppendAsync_NoMessages_TouchesNothing()
        {
            // Arrange
            PostgresConversationStore store = await OpenAsync();

            // Act
            _ = await store.AppendAsync("C1", [], cancellationToken: Token);

            // Assert
            Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM agentcore.conversation_message"));
        }

        [PostgresFact]
        public async Task ItWritesTheStateBesideTheWords()
        {
            PostgresConversationStore store = await OpenAsync();

            ConversationSessionState state = new()
            {
                Stage = "collecting",
                Slots = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
                {
                    ["model"] = JsonValue.Create("F63"),
                },
            };

            _ = await store.AppendAsync(
                "C1",
                [new ConversationMessageDraft(0, new ChatMessage(ChatRole.User, "hello"), "m0")],
                state,
                Token);

            ConversationRecord? record = await store.GetAsync("C1", Token);

            Assert.NotNull(record?.State);
            Assert.Equal("collecting", record.State.Stage);
            Assert.Equal("F63", record.State.Slots["model"]!.GetValue<string>());
        }

        [PostgresFact]
        public async Task AConversationWithNoStateReadsAsNull()
        {
            PostgresConversationStore store = new(DataSource);

            ConversationRecord record = await store.CreateAsync("C3", Token);

            Assert.Null(record.State);
        }

        [PostgresFact]
        public async Task AnAppendWithNoStateLeavesTheStoredStateAlone()
        {
            PostgresConversationStore store = await OpenAsync();

            _ = await store.AppendAsync(
                "C1",
                [new ConversationMessageDraft(0, new ChatMessage(ChatRole.User, "hello"), "m0")],
                new ConversationSessionState { Stage = "collecting" },
                Token);
            _ = await store.AppendAsync(
                "C1",
                [new ConversationMessageDraft(0, new ChatMessage(ChatRole.Assistant, "hi"), "m1")],
                state: null,
                Token);

            ConversationRecord? record = await store.GetAsync("C1", Token);

            Assert.Equal("collecting", record?.State?.Stage);
        }

        [PostgresFact]
        public async Task AnAppendThatFails_LandsNeitherTheWordsNorTheState()
        {
            // Arrange — D5's whole claim: the blob rides the turn's own batch, so the words and the state
            // are of one moment and cannot disagree. Nothing else tests it, and what it rests on is
            // implicit: AppendAsync opens no explicit transaction, and atomicity comes from Npgsql
            // sending the batch between two Sync messages, which makes PostgreSQL wrap it in one
            // implicit transaction of its own. That is a property of the driver, not of this code, so it
            // is worth a test that fails the day a version of Npgsql splits the batch.
            PostgresConversationStore store = await OpenAsync();
            _ = await store.AppendAsync(
                "C1",
                [new ConversationMessageDraft(0, new ChatMessage(ChatRole.User, "hello"), "m0")],
                new ConversationSessionState { Stage = "a" },
                Token);

            // Act — a batch whose FIRST row is new and whose second repeats a message id the conversation
            // already holds. The store owns ordinal assignment now, so a caller can no longer force an
            // ordinal collision; the unique constraint on (conversation_id, message_id) is what fails instead,
            // and "m2" landing would be what a batch that was never one transaction looks like.
            Exception? failure = await Record.ExceptionAsync(
                () => store.AppendAsync(
                    "C1",
                    [
                        new ConversationMessageDraft(1, new ChatMessage(ChatRole.User, "lands first"), "m2"),
                        new ConversationMessageDraft(1, new ChatMessage(ChatRole.User, "again"), "m0"),
                    ],
                    new ConversationSessionState { Stage = "b" },
                    Token).AsTask());

            // Assert — the throw, then the row that had already succeeded, then the state the failed
            // batch tried to write. A stage of "b" beside one message would be a conversation whose blob had
            // moved on without its words; a surviving "m2" would be a batch that was never one
            // transaction.
            Assert.NotNull(failure);
            ConversationRecord? record = await store.GetAsync("C1", Token);
            Assert.Equal("a", record?.State?.Stage);

            IReadOnlyList<ConversationMessage> rows = await store.ReadAllAsync("C1", Token);
            _ = Assert.Single(rows);
            Assert.Equal("m0", rows[0].MessageId);
        }

        [PostgresFact]
        public async Task AStateBlobThatWillNotParse_ReadsAsNoBlobRatherThanClosingTheConversation()
        {
            // Arrange — jsonb refuses malformed JSON, so a bad blob is well-formed JSON of the wrong
            // shape: an older or newer build's column, or a hand-edited row. Version cannot save this
            // one, because Version is only readable after the deserialize has already succeeded.
            PostgresConversationStore store = await OpenAsync();
            await ExecuteAsync("UPDATE agentcore.conversation SET state = '[1, 2, 3]'::jsonb WHERE conversation_id = 'C1'");

            // Act
            ConversationRecord? record = await store.GetAsync("C1", Token);

            // Assert — no throw, and no state. A throw here would escape GetAsync, CreateAsync and
            // OpenSessionAsync, and the conversation could never be opened again by anyone: one bad row would
            // refuse every turn of that call forever, with no diagnostic and no way back.
            Assert.NotNull(record);
            Assert.Null(record.State);
        }

        // ---------------------------------------------------------------------------------------------
        // Rewrite. R4: the record holds the words the caller heard.
        // ---------------------------------------------------------------------------------------------
        [PostgresFact]
        public async Task RewriteAsync_ARow_ReplacesTheWordsOfThatRowOnly()
        {
            // Arrange
            PostgresConversationStore store = await OpenAsync();
            _ = await store.AppendAsync("C1", Turn(turnIndex: 0, idSeed: 0), cancellationToken: Token);

            // Act
            await store.RewriteAsync("C1", "m1", new ChatMessage(ChatRole.Assistant, "Order 41 sh"), Token);

            // Assert
            IReadOnlyList<ConversationMessage> rows = await store.ReadAllAsync("C1", Token);
            Assert.Equal(["what about order 41", "Order 41 sh"], [.. rows.Select(row => row.Content.Text)]);
        }

        [PostgresFact]
        public async Task RewriteAsync_ARow_MovesUpdatedAtAndLeavesCreatedAt()
        {
            // Arrange — the retention sweep reads updated_at, so a corrected turn ages from its
            // correction.
            PostgresConversationStore store = await OpenAsync();
            _ = await store.AppendAsync("C1", Turn(turnIndex: 0, idSeed: 0), cancellationToken: Token);
            await ExecuteAsync("UPDATE agentcore.conversation_message SET created_at = now() - interval '1 hour', updated_at = created_at");

            // Act
            await store.RewriteAsync("C1", "m1", new ChatMessage(ChatRole.Assistant, "Order 41 sh"), Token);

            // Assert
            Assert.True(await ScalarAsync<bool>(
                "SELECT updated_at > created_at FROM agentcore.conversation_message WHERE conversation_id = 'C1' AND message_id = 'm1'"));
        }

        [PostgresFact]
        public async Task RewriteAsync_AMessageIdThatIsNotThere_WritesNothing()
        {
            // Arrange — a barge-in that raced the append it corrects. The append carries the corrected
            // words, so there is nothing to report and nothing to guess at.
            PostgresConversationStore store = await OpenAsync();
            _ = await store.AppendAsync("C1", Turn(turnIndex: 0, idSeed: 0), cancellationToken: Token);

            // Act
            await store.RewriteAsync("C1", "does-not-exist", new ChatMessage(ChatRole.Assistant, "never spoken"), Token);

            // Assert
            Assert.Equal(2L, await ScalarAsync<long>("SELECT count(*) FROM agentcore.conversation_message"));
        }

        // ---------------------------------------------------------------------------------------------
        // Erase, and the sweep.
        // ---------------------------------------------------------------------------------------------
        [PostgresFact]
        public async Task EraseAsync_OneConversation_DeletesItsRowsAndLeavesTheOthers()
        {
            // Arrange
            PostgresConversationStore store = await OpenAsync();
            _ = await store.AppendAsync("C1", Turn(turnIndex: 0, idSeed: 0), cancellationToken: Token);
            _ = await store.AppendAsync("C2", Turn(turnIndex: 0, idSeed: 0), cancellationToken: Token);

            // Act
            int erased = await store.EraseAsync("C1", Token);

            // Assert
            Assert.Equal(2, erased);
            Assert.Empty(await store.ReadAllAsync("C1", Token));
            Assert.Equal(2, (await store.ReadAllAsync("C2", Token)).Count);
        }





        /// <summary>
        /// One ordinary turn: what the caller said, and what the caller heard. <paramref name="idSeed"/>
        /// only keeps message ids unique across appends to the same conversation — the store assigns the
        /// ordinal, so it no longer says where the turn lands.
        /// </summary>
        private static ConversationMessageDraft[] Turn(int turnIndex, int idSeed)
        {
            return [
            new ConversationMessageDraft(turnIndex, new ChatMessage(ChatRole.User, "what about order 41"), $"m{idSeed}"),
            new ConversationMessageDraft(turnIndex, new ChatMessage(ChatRole.Assistant, "Order 41 ships Friday."), $"m{idSeed + 1}"),
        ];
        }
    }
}
