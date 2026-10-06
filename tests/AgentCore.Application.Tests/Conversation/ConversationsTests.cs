using AgentCore.Application.Transcript;
using AgentCore.Application.Blobs;
using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Tests.Fakes;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Conversation
{
    /// <summary>The one door to a stored conversation: its row, its words, and its files go together.</summary>
    public sealed class ConversationsTests
    {
        /// <summary>A reply whose files the capture kept, 3 bytes each.</summary>
        private static ChatMessage ReplyWith(params (string Name, string MediaType)[] kept)
        {
            return new(ChatRole.Assistant, [new TextContent("see"), .. kept.Select(file => Kept(file.Name, file.MediaType))]);
        }

        private static FileContent Kept(string name, string mediaType, long length = 3)
        {
            return new() { Name = name, FileId = "cfile_" + name, MediaType = mediaType, Length = length, Kept = true };
        }

        private static FileContent Refused(string name)
        {
            return new() { Name = name, FileId = "cfile_" + name };
        }

        private static async Task<BlobRef> KeepAsync(RecordingBlobStore blobs, string conversationId, string name, string mediaType)
        {
            return await blobs.PutAsync(new BlobWrite(conversationId, name, mediaType, new MemoryStream([1, 2, 3]), 3), TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task DeleteAsync_DeletesTheFilesAndTheRow()
        {
            // Arrange
            InMemoryConversationStore store = new();
            RecordingBlobStore blobs = new();
            Conversations conversations = new(store, blobs);
            CancellationToken token = TestContext.Current.CancellationToken;
            _ = await conversations.CreateAsync("conversation-1", token);
            _ = await KeepAsync(blobs, "conversation-1", "chart.png", "image/png");
            _ = await KeepAsync(blobs, "conversation-2", "keep.png", "image/png");

            // Act
            await conversations.DeleteAsync("conversation-1", token);

            // Assert
            Assert.Null(await store.GetAsync("conversation-1", token));
            Assert.Equal([("conversation-2", "keep.png")], blobs.Blobs.Keys);
        }

        [Fact]
        public async Task DeleteAsync_NoBlobStore_DeletesTheRowAlone()
        {
            // Arrange
            InMemoryConversationStore store = new();
            Conversations conversations = new(store, blobs: null);
            CancellationToken token = TestContext.Current.CancellationToken;
            _ = await conversations.CreateAsync("conversation-1", token);

            // Act
            await conversations.DeleteAsync("conversation-1", token);

            // Assert
            Assert.Null(await store.GetAsync("conversation-1", token));
        }

        [Fact]
        public async Task LoadWindowAsync_ReturnsTheRowTheWordsAndTheFactsOfEachKeptFile()
        {
            // Arrange
            CountingBlobStore blobs = new();
            Conversations conversations = new(new InMemoryConversationStore(), blobs);
            CancellationToken token = TestContext.Current.CancellationToken;
            _ = await conversations.CreateAsync("conversation-1", token);
            _ = await conversations.AppendMessageAsync("conversation-1", new ChatMessage(ChatRole.User, "chart it"), token);
            _ = await conversations.AppendMessageAsync("conversation-1", ReplyWith(("chart.png", "image/png")), token);

            // Act
            StoredConversation? stored = await conversations.LoadWindowAsync("conversation-1", new TranscriptWindow(null, 10), token);

            // Assert
            Assert.NotNull(stored);
            Assert.Equal("conversation-1", stored.Conversation.ConversationId);
            Assert.Equal(["chart it", "see"], stored.Messages.Select(message => message.Content.Text));
            Assert.Equal(new BlobRef("conversation-1", "chart.png", "image/png", 3), Assert.Single(stored.Files));
        }

        [Fact]
        public async Task LoadWindowAsync_AFullWindow_NamesTheTurnToReadBeforeNext_AndListsThatWindowsFilesAlone()
        {
            // Arrange: turn 0 kept a file, turn 1 kept another. The window holds turn 1 only.
            CountingBlobStore blobs = new();
            Conversations conversations = new(new InMemoryConversationStore(), blobs);
            CancellationToken token = TestContext.Current.CancellationToken;
            _ = await conversations.CreateAsync("conversation-1", token);
            _ = await conversations.AppendAsync(
                "conversation-1",
                [
                    new ConversationMessageDraft(0, new ChatMessage(ChatRole.User, "chart it"), "m0"),
                    new ConversationMessageDraft(0, ReplyWith(("chart.png", "image/png")), "m1"),
                    new ConversationMessageDraft(1, new ChatMessage(ChatRole.User, "rows too"), "m2"),
                    new ConversationMessageDraft(1, ReplyWith(("rows.csv", "text/csv")), "m3"),
                ],
                cancellationToken: token);

            // Act
            StoredConversation? stored = await conversations.LoadWindowAsync("conversation-1", new TranscriptWindow(null, 1), token);

            // Assert
            Assert.NotNull(stored);
            Assert.Equal(["m2", "m3"], stored.Messages.Select(message => message.MessageId));
            Assert.Equal(1, stored.OlderBefore);
            Assert.Equal("rows.csv", Assert.Single(stored.Files).Name);
        }

        [Fact]
        public async Task LoadWindowAsync_AShortWindow_ReachedTheStart_AndNamesNoTurn()
        {
            // Arrange
            Conversations conversations = new(new InMemoryConversationStore(), blobs: null);
            CancellationToken token = TestContext.Current.CancellationToken;
            _ = await conversations.CreateAsync("conversation-1", token);
            _ = await conversations.AppendMessageAsync("conversation-1", new ChatMessage(ChatRole.User, "hello"), token);

            // Act
            StoredConversation? stored = await conversations.LoadWindowAsync("conversation-1", new TranscriptWindow(null, 5), token);

            // Assert
            Assert.NotNull(stored);
            _ = Assert.Single(stored.Messages);
            Assert.Null(stored.OlderBefore);
        }

        [Fact]
        public async Task LoadWindowAsync_AConversationThatWasNeverMade_IsNull()
        {
            // Arrange
            Conversations conversations = new(new InMemoryConversationStore(), blobs: null);

            // Act
            StoredConversation? stored = await conversations.LoadWindowAsync("conversation-1", new TranscriptWindow(null, 5), TestContext.Current.CancellationToken);

            // Assert
            Assert.Null(stored);
        }

        [Fact]
        public void KeptFiles_ListsFromTheContent_EachOnce_InFirstSeenOrder_WithoutAskingTheStore()
        {
            // Arrange: the store is empty on purpose. The content is the index.
            CountingBlobStore blobs = new();
            Conversations conversations = new(new InMemoryConversationStore(), blobs);

            // Act
            IReadOnlyList<BlobRef> files = conversations.KeptFiles(
                "conversation-1",
                [
                    new ChatMessage(ChatRole.Assistant, [Kept("chart.png", "image/png"), Refused("lost.pdf")]),
                    ReplyWith(("rows.csv", "text/csv"), ("chart.png", "image/png")),
                ]);

            // Assert
            Assert.Equal(
                [new BlobRef("conversation-1", "chart.png", "image/png", 3), new BlobRef("conversation-1", "rows.csv", "text/csv", 3)],
                files);
            Assert.Equal(0, blobs.Stats);
        }

        [Fact]
        public void KeptFiles_NoBlobStore_ListsNothing()
        {
            // Arrange
            Conversations conversations = new(new InMemoryConversationStore(), blobs: null);

            // Act
            IReadOnlyList<BlobRef> files = conversations.KeptFiles("conversation-1", [ReplyWith(("chart.png", "image/png"))]);

            // Assert
            Assert.Empty(files);
        }

        [Fact]
        public void KeptFiles_SameNameKeptTwice_TheLaterFactsWinInTheFirstPlace()
        {
            // Arrange: the model redrew chart.png after rows.csv; the store replaced the first.
            Conversations conversations = new(new InMemoryConversationStore(), new RecordingBlobStore());
            ChatMessage reply = new(ChatRole.Assistant, [Kept("chart.png", "image/png", 10), Kept("rows.csv", "text/csv", 5), Kept("chart.png", "image/png", 20)]);

            // Act
            IReadOnlyList<BlobRef> files = conversations.KeptFiles("conversation-1", [reply]);

            // Assert
            Assert.Equal(
                [new BlobRef("conversation-1", "chart.png", "image/png", 20), new BlobRef("conversation-1", "rows.csv", "text/csv", 5)],
                files);
        }

        [Theory]
        [InlineData("../etc/passwd")]
        [InlineData("a/b.png")]
        [InlineData("")]
        public void KeptFiles_UnsafeName_IsNotListed(string name)
        {
            // Arrange
            Conversations conversations = new(new InMemoryConversationStore(), new RecordingBlobStore());

            // Act
            IReadOnlyList<BlobRef> files = conversations.KeptFiles("conversation-1", [new ChatMessage(ChatRole.Assistant, [Kept(name, "image/png")])]);

            // Assert
            Assert.Empty(files);
        }

        [Fact]
        public async Task LinkFileAsync_AKeptBlob_ReturnsALinkMintedForFiveMinutes()
        {
            // Arrange
            RecordingBlobStore blobs = new();
            Conversations conversations = new(new InMemoryConversationStore(), blobs);
            _ = await KeepAsync(blobs, "conversation-1", "chart.png", "image/png");

            // Act
            Uri? url = await conversations.LinkFileAsync("conversation-1", "chart.png", TestContext.Current.CancellationToken);

            // Assert: the lifetime is the owner's.
            Assert.Equal("https://blobs.test/conversation-1/chart.png?ttl=300", url?.ToString());
        }

        [Fact]
        public async Task LinkFileAsync_ABlobAnotherConversationKept_IsNull()
        {
            // Arrange
            RecordingBlobStore blobs = new();
            Conversations conversations = new(new InMemoryConversationStore(), blobs);
            _ = await KeepAsync(blobs, "conversation-2", "chart.png", "image/png");

            // Act
            Uri? url = await conversations.LinkFileAsync("conversation-1", "chart.png", TestContext.Current.CancellationToken);

            // Assert
            Assert.Null(url);
        }

        [Fact]
        public async Task LinkFileAsync_NoSuchBlob_IsNullAndNothingIsSigned()
        {
            // Arrange
            CountingBlobStore blobs = new();
            Conversations conversations = new(new InMemoryConversationStore(), blobs);

            // Act
            Uri? url = await conversations.LinkFileAsync("conversation-1", "chart.png", TestContext.Current.CancellationToken);

            // Assert
            Assert.Null(url);
            Assert.Equal(1, blobs.Stats);
            Assert.Equal(0, blobs.Signed);
        }

        [Theory]
        [InlineData("../etc/passwd")]
        [InlineData("a/b.png")]
        [InlineData("")]
        public async Task LinkFileAsync_UnsafeName_IsNullAndTheStoreIsNotAsked(string name)
        {
            // Arrange
            CountingBlobStore blobs = new();
            Conversations conversations = new(new InMemoryConversationStore(), blobs);

            // Act
            Uri? url = await conversations.LinkFileAsync("conversation-1", name, TestContext.Current.CancellationToken);

            // Assert
            Assert.Null(url);
            Assert.Equal(0, blobs.Stats);
        }

        [Fact]
        public async Task LinkFileAsync_BlobExistsButTheStoreHasNoWebDoor_IsNull()
        {
            // Arrange
            CountingBlobStore blobs = new() { Holds = new BlobRef("conversation-1", "chart.png", "image/png", 3), WebDoor = false };
            Conversations conversations = new(new InMemoryConversationStore(), blobs);

            // Act
            Uri? url = await conversations.LinkFileAsync("conversation-1", "chart.png", TestContext.Current.CancellationToken);

            // Assert
            Assert.Null(url);
        }

        [Theory]
        [InlineData("a/b")]
        [InlineData("")]
        [InlineData("  ")]
        public async Task LinkFileAsync_AConversationIdThatCannotBeAKey_IsNullAndTheStoreIsNotAsked(string conversationId)
        {
            // Arrange
            CountingBlobStore blobs = new();
            Conversations conversations = new(new InMemoryConversationStore(), blobs);

            // Act
            Uri? url = await conversations.LinkFileAsync(conversationId, "chart.png", TestContext.Current.CancellationToken);

            // Assert
            Assert.Null(url);
            Assert.Equal(0, blobs.Stats);
        }

        [Fact]
        public async Task LinkFileAsync_NoBlobStore_IsNull()
        {
            // Arrange
            Conversations conversations = new(new InMemoryConversationStore(), blobs: null);

            // Act
            Uri? url = await conversations.LinkFileAsync("conversation-1", "chart.png", TestContext.Current.CancellationToken);

            // Assert
            Assert.Null(url);
        }

        /// <summary>Counts how often anyone asks it whether a blob exists and how often anyone signs one. It holds one blob at most, and can lack a web door.</summary>
        private sealed class CountingBlobStore : IBlobStore
        {
            public int Stats { get; private set; }

            public int Signed { get; private set; }

            public BlobRef? Holds { get; init; }

            public bool WebDoor { get; init; } = true;

            public ValueTask<BlobRef> PutAsync(BlobWrite write, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public ValueTask<BlobRead?> OpenReadAsync(string ownerId, string name, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public ValueTask<BlobRef?> StatAsync(string ownerId, string name, CancellationToken cancellationToken = default)
            {
                Stats++;
                return ValueTask.FromResult(Holds);
            }

            public ValueTask<Uri?> LinkAsync(BlobRef blob, TimeSpan lifetime, CancellationToken cancellationToken = default)
            {
                Signed++;
                return ValueTask.FromResult<Uri?>(!WebDoor ? null : new Uri($"https://blobs.test/{blob.OwnerId}/{blob.Name}?ttl={(int)lifetime.TotalSeconds}"));
            }

            public ValueTask DeleteByOwnerAsync(string ownerId, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }
        }
    }
}
