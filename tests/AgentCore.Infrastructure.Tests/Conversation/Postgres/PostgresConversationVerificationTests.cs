using AgentCore.Application.Conversation;
using AgentCore.Application.Transcript;
using AgentCore.Domain.Audit;
using AgentCore.Domain.Sources;
using AgentCore.Infrastructure.Audit.Postgres;
using AgentCore.Infrastructure.Conversation.Postgres;
using AgentCore.Infrastructure.Tests.Database.Postgres;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Conversation.Postgres
{
    /// <summary>
    /// The conversation store against the audit store: what <see cref="PostgresConversationVerification.ReadSpokenTurnsAsync"/>
    /// rebuilds from the words, beside the hash the audit chain holds for them.
    /// </summary>
    public sealed class PostgresConversationVerificationTests : PostgresDatabaseTest
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

        [PostgresFact]
        public async Task ReadSpokenTurnsAsync_AToolCallingTurn_ReturnsOneRowForThatTurn()
        {
            // Arrange — the DISTINCT ON guard. The turn writes an assistant message carrying the tool
            // conversation and another carrying the reply; without it both come back, and the textless one
            // reports a false tamper on every tool-calling turn.
            PostgresConversationStore store = await OpenAsync();
            _ = await WriteToolCallingTurnAsync(store, "C1", turnIndex: 0, spoken: "Order 41 ships Friday.");

            // Act
            IReadOnlyList<TranscriptTurnDigest> turns = await store.ReadSpokenTurnsAsync("C1", Token);

            // Assert
            TranscriptTurnDigest turn = Assert.Single(turns);
            Assert.Equal("Order 41 ships Friday.", turn.Spoken);
        }

        [PostgresFact]
        public async Task ReadSpokenTurnsAsync_AToolCallingTurn_MatchesTheHashTheChainHolds()
        {
            // Arrange
            PostgresConversationStore store = await OpenAsync();
            _ = await WriteToolCallingTurnAsync(store, "C1", turnIndex: 0, spoken: "Order 41 ships Friday.");

            // Act
            IReadOnlyList<TranscriptTurnDigest> turns = await store.ReadSpokenTurnsAsync("C1", Token);

            // Assert — what the nightly check does: hash the words the conversation store holds, and compare.
            Assert.Equal(AuditHash.OfText(turns[0].Spoken).Value, turns[0].ReplyTextSha256);
        }

        [PostgresFact]
        public async Task ReadSpokenTurnsAsync_AToolCallingTurnThatAlsoCited_MatchesTheHashTheChainHolds()
        {
            // Arrange — a SourceContent rides the tool-result message alongside its FunctionResultContent.
            // The verify query never looks at that row's role, but jsonb round-tripping a second content
            // type on it must not upset the DISTINCT ON guard or the hash comparison.
            PostgresConversationStore store = await OpenAsync();
            _ = await WriteToolCallingTurnAsync(store, "C1", turnIndex: 0, spoken: "Order 41 ships Friday.", cited: true);

            // Act
            IReadOnlyList<TranscriptTurnDigest> turns = await store.ReadSpokenTurnsAsync("C1", Token);

            // Assert
            TranscriptTurnDigest turn = Assert.Single(turns);
            Assert.Equal(AuditHash.OfText(turn.Spoken).Value, turn.ReplyTextSha256);
        }

        [PostgresFact]
        public async Task ReadSpokenTurnsAsync_ATurnWhoseWordsWereErased_ReturnsNoRowForIt()
        {
            // Arrange — the erasure working, and not a tamper. The chain keeps its row and goes on
            // proving what it proved.
            PostgresConversationStore store = await OpenAsync();
            _ = await WriteToolCallingTurnAsync(store, "C1", turnIndex: 0, spoken: "Order 41 ships Friday.");
            _ = await store.EraseAsync("C1", Token);

            // Act
            IReadOnlyList<TranscriptTurnDigest> turns = await store.ReadSpokenTurnsAsync("C1", Token);

            // Assert
            Assert.Empty(turns);
            Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM agentcore.audit_event"));
        }

        [PostgresFact]
        public async Task ReadSpokenTurnsAsync_ATurnAmendedInTheChain_ReturnsOneRowHoldingTheLatestHash()
        {
            // Arrange — a barge-in amends a turn, so the chain carries a second turn.completed for it.
            // Without the guard on the chain side the join multiplies and one turn answers twice.
            PostgresConversationStore store = await OpenAsync();
            Guid firstEventId = await WriteToolCallingTurnAsync(store, "C1", turnIndex: 0, spoken: "Order 41 ships Friday.");
            await store.RewriteAsync("C1", "m3", new ChatMessage(ChatRole.Assistant, "Order 41 sh"), Token);
            await AmendTurnAsync("C1", turnIndex: 0, amends: firstEventId, spoken: "Order 41 sh");

            // Act
            IReadOnlyList<TranscriptTurnDigest> turns = await store.ReadSpokenTurnsAsync("C1", Token);

            // Assert
            TranscriptTurnDigest turn = Assert.Single(turns);
            Assert.Equal(AuditHash.OfText("Order 41 sh").Value, turn.ReplyTextSha256);
        }

        [PostgresFact]
        public async Task ReadSpokenTurnsAsync_AnEarlierStepTampered_NoLongerMatchesTheHash()
        {
            // Arrange — a multi-step turn: the model announces the lookup in prose on the same message as
            // its call, then answers once the result is in. Both text rows belong to the reply, so the
            // baseline must already match before the tamper proves anything.
            PostgresConversationStore store = await OpenAsync();
            _ = await WriteMultiStepTurnAsync(
                store, "C1", turnIndex: 0, announced: "Let me check that.", spoken: "Order 41 ships Friday.");

            TranscriptTurnDigest before = Assert.Single(await store.ReadSpokenTurnsAsync("C1", Token));
            Assert.Equal(AuditHash.OfText(before.Spoken).Value, before.ReplyTextSha256);

            // Act — tamper the EARLIER step's row (m1), not the last one.
            await store.RewriteAsync(
                "C1",
                "m1",
                new ChatMessage(ChatRole.Assistant, [new TextContent("Something else."), new FunctionCallContent("id1", "lookup", null)]),
                Token);

            // Assert — a chain that only ever proved the last row would miss this.
            TranscriptTurnDigest after = Assert.Single(await store.ReadSpokenTurnsAsync("C1", Token));
            Assert.NotEqual(AuditHash.OfText(after.Spoken).Value, after.ReplyTextSha256);
        }

        // A phone turn writes what the vendor's front voice said ahead of the caller's words; the chain hashes only
        // the agent's reply, so the rebuild must leave those lines out, found by the author stored in the jsonb.
        [PostgresFact]
        public async Task ReadSpokenTurnsAsync_ATurnThatCarriesFrontVoiceLines_MatchesTheHashOfTheAgentsReplyOnly()
        {
            PostgresConversationStore store = await OpenAsync();
            _ = await store.AppendAsync(
                "C1",
                [
                    new ConversationMessageDraft(0, new ChatMessage(ChatRole.User, "do you know what day it is today"), "m0"),
                    new ConversationMessageDraft(0, new ChatMessage(ChatRole.Assistant, "It's Wednesday.") { AuthorName = "front_voice" }, "m1"),
                    new ConversationMessageDraft(0, new ChatMessage(ChatRole.User, "please end the call now"), "m2"),
                    new ConversationMessageDraft(0, new ChatMessage(ChatRole.Assistant, "Today is Sunday. Goodbye!"), "m3"),
                ],
                cancellationToken: Token);
            await new PostgresAuditSink(DataSource).AppendAsync(
                new AuditEvent
                {
                    ConversationId = "C1",
                    EventId = Guid.CreateVersion7(),
                    Kind = AuditEventKind.TurnCompleted,
                    OccurredAt = new DateTimeOffset(2026, 10, 4, 9, 0, 1, TimeSpan.Zero),
                    TurnIndex = 0,
                    Payload = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [AuditPayloadKeys.ReplyTextSha256] = AuditHash.OfText("Today is Sunday. Goodbye!").Value,
                    },
                },
                Token);

            TranscriptTurnDigest turn = Assert.Single(await store.ReadSpokenTurnsAsync("C1", Token));

            Assert.Equal("Today is Sunday. Goodbye!", turn.Spoken);
            Assert.Equal(AuditHash.OfText(turn.Spoken).Value, turn.ReplyTextSha256);
        }

        /// <summary>Writes a tool-calling turn to the conversation store and its <c>turn.completed</c> row to the audit store.</summary>
        private async Task<Guid> WriteToolCallingTurnAsync(
            PostgresConversationStore store, string conversationId, int turnIndex, string spoken, bool cited = false)
        {
            List<AIContent> toolResultContents = [new FunctionResultContent("id1", "Friday")];
            if (cited)
            {
                toolResultContents.Add(new SourceContent
                {
                    Source = new SourceReference
                    {
                        SourceId = "card-42",
                        Kind = SourceKind.Document,
                        Title = "Order lookup",
                        Origin = "knowledge",
                    },
                    CallId = "id1",
                });
            }

            _ = await store.AppendAsync(
                conversationId,
                [
                    new ConversationMessageDraft(turnIndex, new ChatMessage(ChatRole.User, "what about order 41"), "m0"),
                    new ConversationMessageDraft(turnIndex, new ChatMessage(
                        ChatRole.Assistant, [new FunctionCallContent("id1", "lookup", null)]), "m1"),
                    new ConversationMessageDraft(turnIndex, new ChatMessage(ChatRole.Tool, toolResultContents), "m2"),
                    new ConversationMessageDraft(turnIndex, new ChatMessage(ChatRole.Assistant, spoken), "m3"),
                ],
                cancellationToken: Token);

            // Not disposed: the sink would take the test's own pool with it.
            Guid eventId = Guid.CreateVersion7();
            PostgresAuditSink chain = new(DataSource);
            await chain.AppendAsync(
                new AuditEvent
                {
                    ConversationId = conversationId,
                    EventId = eventId,
                    Kind = AuditEventKind.TurnCompleted,
                    OccurredAt = new DateTimeOffset(2026, 8, 19, 9, 0, 1, TimeSpan.Zero),
                    TurnIndex = turnIndex,
                    Payload = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [AuditPayloadKeys.ReplyTextSha256] = AuditHash.OfText(spoken).Value,
                    },
                },
                Token);

            return eventId;
        }

        /// <summary>
        /// Writes a two-step turn to the conversation store — the tool announced on the same message as its call, then the
        /// reply once the result is in — and its <c>turn.completed</c> row to the audit store, hashed over both steps.
        /// </summary>
        private async Task<Guid> WriteMultiStepTurnAsync(
            PostgresConversationStore store, string conversationId, int turnIndex, string announced, string spoken)
        {
            _ = await store.AppendAsync(
                conversationId,
                [
                    new ConversationMessageDraft(turnIndex, new ChatMessage(ChatRole.User, "what about order 41"), "m0"),
                    new ConversationMessageDraft(turnIndex, new ChatMessage(
                        ChatRole.Assistant, [new TextContent(announced), new FunctionCallContent("id1", "lookup", null)]), "m1"),
                    new ConversationMessageDraft(turnIndex, new ChatMessage(ChatRole.Tool, [new FunctionResultContent("id1", "Friday")]), "m2"),
                    new ConversationMessageDraft(turnIndex, new ChatMessage(ChatRole.Assistant, spoken), "m3"),
                ],
                cancellationToken: Token);

            // Not disposed: the sink would take the test's own pool with it.
            Guid eventId = Guid.CreateVersion7();
            PostgresAuditSink chain = new(DataSource);
            await chain.AppendAsync(
                new AuditEvent
                {
                    ConversationId = conversationId,
                    EventId = eventId,
                    Kind = AuditEventKind.TurnCompleted,
                    OccurredAt = new DateTimeOffset(2026, 8, 19, 9, 0, 1, TimeSpan.Zero),
                    TurnIndex = turnIndex,
                    Payload = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [AuditPayloadKeys.ReplyTextSha256] = AuditHash.OfText(announced + spoken).Value,
                    },
                },
                Token);

            return eventId;
        }

        /// <summary>Writes a second <c>turn.completed</c> that corrects the first, as a barge-in does.</summary>
        private async Task AmendTurnAsync(string conversationId, int turnIndex, Guid amends, string spoken)
        {
            // Not disposed: the sink would take the test's own pool with it.
            PostgresAuditSink chain = new(DataSource);
            await chain.AppendAsync(
                new AuditEvent
                {
                    ConversationId = conversationId,
                    EventId = Guid.CreateVersion7(),
                    Kind = AuditEventKind.TurnCompleted,
                    OccurredAt = new DateTimeOffset(2026, 8, 19, 9, 0, 2, TimeSpan.Zero),
                    TurnIndex = turnIndex,
                    AmendsEventId = amends,
                    Payload = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [AuditPayloadKeys.ReplyTextSha256] = AuditHash.OfText(spoken).Value,
                    },
                },
                Token);
        }
    }
}
