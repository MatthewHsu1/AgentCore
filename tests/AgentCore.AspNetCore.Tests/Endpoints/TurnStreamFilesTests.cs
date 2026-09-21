using AgentCore.Application.Transcript;
using AgentCore.Application.Blobs;
using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Ports;
using AgentCore.AspNetCore.Endpoints;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Endpoints
{
    /// <summary>Which published files reach the browser as a part, and with what link.</summary>
    public sealed class TurnStreamFilesTests
    {
        private static ChatResponseUpdate UpdateWith(params AIContent[] contents)
        {
            return new(ChatRole.Assistant, contents);
        }

        /// <summary>A file the publish tool kept, as it files it on the turn before the stream ends.</summary>
        private static FileContent Kept(string name, string mediaType, long length)
        {
            return new() { Name = name, FileId = "cfile_" + name, MediaType = mediaType, Length = length, Kept = true };
        }

        [Fact]
        public async Task ResolveAsync_NotedFileTheCaptureKept_YieldsOnePartWithTheLink()
        {
            // Arrange: the same content passes twice, as a re-yielded update would; a refused one passes too.
            Conversations conversations = new(new InMemoryConversationStore(), new StubBlobStore());
            TurnStreamFiles files = new();
            FileContent chart = Kept("chart.png", "image/png", 48213);
            FileContent refused = new() { Name = "refused.png", FileId = "cfile_2" };
            files.Note(UpdateWith(new TextContent("see"), chart));
            files.Note(UpdateWith(chart));
            files.Note(UpdateWith(refused));

            // Act
            List<TurnStreamFile> parts = await files.ResolveAsync(conversations, "conversation-1", TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

            // Assert: once, linked, and the refused file is not there.
            TurnStreamFile part = Assert.Single(parts);
            Assert.Equal("chart.png", part.Payload.Name);
            Assert.Equal("image/png", part.Payload.MediaType);
            Assert.Equal(48213, part.Payload.Length);
            Assert.Equal("https://blobs.test/conversation-1/chart.png?ttl=900", part.Payload.Url);
        }

        [Fact]
        public async Task ResolveAsync_StoreWithNoWebDoor_YieldsThePartWithoutAUrl()
        {
            // Arrange
            Conversations conversations = new(new InMemoryConversationStore(), new StubBlobStore { Links = false });
            TurnStreamFiles files = new();
            files.Note(UpdateWith(Kept("rows.csv", "text/csv", 8)));

            // Act
            List<TurnStreamFile> parts = await files.ResolveAsync(conversations, "conversation-1", TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Null(Assert.Single(parts).Payload.Url);
        }

        [Fact]
        public async Task ResolveAsync_NothingNoted_YieldsNothing()
        {
            // Arrange
            Conversations conversations = new(new InMemoryConversationStore(), blobs: null);

            // Act
            List<TurnStreamFile> parts = await new TurnStreamFiles().ResolveAsync(conversations, "conversation-1", TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Empty(parts);
        }

        private sealed class StubBlobStore : IBlobStore
        {
            public bool Links { get; init; } = true;

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
                throw new NotSupportedException("The read path links from the content and never asks the store.");
            }

            public ValueTask<Uri?> LinkAsync(BlobRef blob, TimeSpan lifetime, CancellationToken cancellationToken = default)
            {
                return ValueTask.FromResult(Links ? new Uri($"https://blobs.test/{blob.OwnerId}/{blob.Name}?ttl={(int)lifetime.TotalSeconds}") : null);
            }

            public ValueTask DeleteByOwnerAsync(string ownerId, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }
        }
    }
}
