using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Transcript;
using AgentCore.Application.Tests.Runtime;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Conversation
{
    /// <summary>The conversation store, kept in this process.</summary>
    public sealed class InMemoryConversationStoreTests
    {
        private static CancellationToken Token => TestContext.Current.CancellationToken;

        [Fact]
        public async Task CreateAsync_TheSameIdTwice_IsOneConversation()
        {
            // Arrange
            InMemoryConversationStore store = new();

            // Act
            ConversationRecord first = await store.CreateAsync("c1", Token);
            ConversationRecord second = await store.CreateAsync("c1", Token);

            // Assert
            Assert.Equal(first.ConversationId, second.ConversationId);
            Assert.Equal(first.CreatedAt, second.CreatedAt);
        }

        [Fact]
        public async Task GetAsync_AConversationThatWasNeverMade_IsNull()
        {
            // Arrange
            InMemoryConversationStore store = new();

            // Act
            ConversationRecord? found = await store.GetAsync("missing", Token);

            // Assert
            Assert.Null(found);
        }

        [Fact]
        public async Task RenameAsync_AConversation_ChangesOnlyItsTitle()
        {
            // Arrange
            InMemoryConversationStore store = new();
            _ = await store.CreateAsync("c1", Token);

            // Act
            await store.RenameAsync("c1", "A squeaky belt", Token);

            // Assert
            ConversationRecord? found = await store.GetAsync("c1", Token);
            Assert.Equal("A squeaky belt", found!.Title);
            Assert.Equal(ConversationStatus.Regular, found.Status);
        }

        [Fact]
        public async Task ListAsync_APrincipalWithNoConversations_IsEmptyAndHasNoCursor()
        {
            // Arrange
            InMemoryConversationStore store = new();
            _ = await store.CreateAsync("c1", Token);

            // Act
            ConversationPage page = await store.ListAsync("nobody", after: null, limit: 10, cancellationToken: Token);

            // Assert
            Assert.Empty(page.Conversations);
            Assert.Null(page.NextCursor);
        }

        [Fact]
        public async Task ListAsync_AnotherPrincipalsConversation_IsNotReturned()
        {
            // Arrange
            InMemoryConversationStore store = new();
            _ = await store.CreateAsync("mine", Token);
            _ = await store.CreateAsync("theirs", Token);
            await store.AttachPrincipalAsync("mine", "person-a", "caller", Token);
            await store.AttachPrincipalAsync("theirs", "person-b", "caller", Token);

            // Act
            ConversationPage page = await store.ListAsync("person-a", after: null, limit: 10, cancellationToken: Token);

            // Assert
            Assert.Equal(["mine"], page.Conversations.Select(conversation => conversation.ConversationId));
        }

        [Fact]
        public async Task ListAsync_TwoKeysOnOneConversation_FindsItByEither()
        {
            // Arrange
            InMemoryConversationStore store = new();
            _ = await store.CreateAsync("c1", Token);
            await store.AttachPrincipalAsync("c1", "tenant-a", "tenant", Token);
            await store.AttachPrincipalAsync("c1", "person-a", "caller", Token);

            // Act
            ConversationPage byTenant = await store.ListAsync("tenant-a", after: null, limit: 10, cancellationToken: Token);
            ConversationPage byPerson = await store.ListAsync("person-a", after: null, limit: 10, cancellationToken: Token);

            // Assert
            _ = Assert.Single(byTenant.Conversations);
            _ = Assert.Single(byPerson.Conversations);
        }

        [Fact]
        public async Task AttachPrincipalAsync_TheSamePairTwice_IsOneAttachment()
        {
            // Arrange
            InMemoryConversationStore store = new();
            _ = await store.CreateAsync("c1", Token);

            // Act
            await store.AttachPrincipalAsync("c1", "person-a", "caller", Token);
            await store.AttachPrincipalAsync("c1", "person-a", "caller", Token);

            // Assert
            ConversationPage page = await store.ListAsync("person-a", after: null, limit: 10, cancellationToken: Token);
            _ = Assert.Single(page.Conversations);
        }

        [Fact]
        public async Task DetachPrincipalAsync_TheOnlyKey_LeavesTheConversationUnlisted()
        {
            // Arrange
            InMemoryConversationStore store = new();
            _ = await store.CreateAsync("c1", Token);
            await store.AttachPrincipalAsync("c1", "person-a", "caller", Token);

            // Act
            await store.DetachPrincipalAsync("c1", "person-a", Token);

            // Assert
            Assert.Empty((await store.ListAsync("person-a", after: null, limit: 10, cancellationToken: Token)).Conversations);
            Assert.NotNull(await store.GetAsync("c1", Token));
        }

        [Fact]
        public async Task ListAsync_ArchivedConversations_AreOutOfARegularListing()
        {
            // Arrange
            InMemoryConversationStore store = new();
            _ = await store.CreateAsync("c1", Token);
            await store.AttachPrincipalAsync("c1", "person-a", "caller", Token);
            await store.SetStatusAsync("c1", ConversationStatus.Archived, Token);

            // Act
            ConversationPage regular = await store.ListAsync("person-a", after: null, limit: 10, ConversationStatus.Regular, Token);
            ConversationPage everything = await store.ListAsync("person-a", after: null, limit: 10, cancellationToken: Token);

            // Assert
            Assert.Empty(regular.Conversations);
            _ = Assert.Single(everything.Conversations);
        }

        [Fact]
        public async Task ListAsync_MoreConversationsThanTheLimit_PagesWithNoGapAndNoRepeat()
        {
            // Arrange
            InMemoryConversationStore store = new();
            for (int i = 0; i < 5; i++)
            {
                _ = await store.CreateAsync($"c{i}", Token);
                await store.AttachPrincipalAsync($"c{i}", "person-a", "caller", Token);
            }

            // Act
            List<string> seen = [];
            string? cursor = null;
            do
            {
                ConversationPage page = await store.ListAsync("person-a", cursor, limit: 2, cancellationToken: Token);
                seen.AddRange(page.Conversations.Select(conversation => conversation.ConversationId));
                cursor = page.NextCursor;
            }
            while (cursor is not null);

            // Assert
            Assert.Equal(5, seen.Count);
            Assert.Equal(5, seen.Distinct().Count());
        }

        [Fact]
        public async Task SetExternalIdAsync_AConsumersOwnId_IsReadBack()
        {
            // Arrange
            InMemoryConversationStore store = new();
            _ = await store.CreateAsync("c1", Token);

            // Act
            await store.SetExternalIdAsync("c1", "crm-77", Token);

            // Assert
            Assert.Equal("crm-77", (await store.GetAsync("c1", Token))!.ExternalId);
        }

        [Fact]
        public async Task SetExternalIdAsync_Null_ClearsTheId()
        {
            // Arrange
            InMemoryConversationStore store = new();
            _ = await store.CreateAsync("c1", Token);
            await store.SetExternalIdAsync("c1", "crm-77", Token);

            // Act
            await store.SetExternalIdAsync("c1", null, Token);

            // Assert
            Assert.Null((await store.GetAsync("c1", Token))!.ExternalId);
        }

        [Fact]
        public async Task DeleteAsync_AConversation_TakesItsAttachmentsWithIt()
        {
            // Arrange
            InMemoryConversationStore store = new();
            _ = await store.CreateAsync("c1", Token);
            await store.AttachPrincipalAsync("c1", "person-a", "caller", Token);

            // Act
            await store.DeleteAsync("c1", Token);

            // Assert
            Assert.Null(await store.GetAsync("c1", Token));
            Assert.Empty((await store.ListAsync("person-a", after: null, limit: 10, cancellationToken: Token)).Conversations);
        }

        [Fact]
        public async Task DeleteAsync_AConversation_TakesItsResumeStateWithIt()
        {
            // Arrange — the state lives beside the row rather than on it, so it has to be forgotten by
            // hand. In PostgreSQL it is a column of the conversation row and DELETE FROM conversation takes it, and the
            // two backings answering differently is the defect this pins.
            InMemoryConversationStore store = new();
            _ = await store.CreateAsync("c1", Token);
            _ = await store.AppendAsync("c1", [Word()], new ConversationSessionState { Stage = "collecting" }, Token);

            // Act
            await store.DeleteAsync("c1", Token);

            // Assert — a conversation made again under the same id is a NEW conversation, not the dead one resurrected
            // with its stage and the caller data the extractor left in its slots.
            Assert.Null((await store.CreateAsync("c1", Token)).State);
        }

        [Fact]
        public async Task DeleteAsync_AConversation_TakesItsResponseContinuationsWithIt()
        {
            // Arrange — a response id's row hangs off the conversation it continues; deleting the
            // conversation must forget it too, or a later turn under a reused id could resolve a dead one.
            InMemoryConversationStore store = new();
            _ = await store.CreateAsync("c1", Token);
            await store.SaveContinuationAsync("resp_1", "c1", Token);

            // Act
            await store.DeleteAsync("c1", Token);

            // Assert
            Assert.Null(await store.FindContinuationAsync("resp_1", Token));
        }

        [Fact]
        public async Task SweepAsync_AResponseIdPastRetention_TakesTheRowOnly()
        {
            // Arrange — retention ages out response ids, not the conversation they continue: the words and
            // the slots the extractor filled stay, the same way a stale response id stops resolving while
            // the conversation itself lives on.
            TestTimeProvider clock = new();
            InMemoryConversationStore store = new(clock);
            _ = await store.CreateAsync("c1", Token);
            _ = await store.AppendAsync("c1", [Word()], new ConversationSessionState { Stage = "collecting" }, Token);
            await store.SaveContinuationAsync("resp_1", "c1", Token);

            // Act
            clock.Advance(TimeSpan.FromDays(2));
            int swept = await store.SweepAsync(TimeSpan.FromDays(1), cancellationToken: Token);

            // Assert
            Assert.Equal(1, swept);
            Assert.Null(await store.FindContinuationAsync("resp_1", Token));
            Assert.NotNull(await store.GetAsync("c1", Token));
        }

        [Fact]
        public async Task FindContinuationAsync_AConversationIdWithNoResponseRow_IsNull()
        {
            // Arrange — D3: a conversation id is never itself a response_continuation row, so resolving one
            // to "its own" continuation finds nothing; a caller falls back to GetAsync for that.
            InMemoryConversationStore store = new();
            _ = await store.CreateAsync("c1", Token);
            await store.SaveContinuationAsync("resp_1", "c1", Token);

            // Act & Assert
            Assert.Null(await store.FindContinuationAsync("c1", Token));
        }

        private static ConversationMessageDraft Word()
        {
            return new(0, new ChatMessage(ChatRole.User, "hello"), "m0");
        }
    }
}
