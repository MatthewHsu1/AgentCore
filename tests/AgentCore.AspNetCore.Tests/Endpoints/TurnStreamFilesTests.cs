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
    /// <summary>Which published files reach the browser as a part, and with what facts.</summary>
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
        public void Resolve_NotedFileTheCaptureKept_YieldsOnePartWithTheFacts()
        {
            // Arrange: the same content passes twice, as a re-yielded update would; a refused one passes too.
            StubBlobStore blobs = new();
            Conversations conversations = new(new InMemoryConversationStore(), blobs);
            TurnStreamFiles files = new();
            FileContent chart = Kept("chart.png", "image/png", 48213);
            chart.Title = "Chart";
            FileContent refused = new() { Name = "refused.png", FileId = "cfile_2" };
            files.Note(UpdateWith(new TextContent("see"), chart));
            files.Note(UpdateWith(chart));
            files.Note(UpdateWith(refused));

            // Act
            List<TurnStreamPart> parts = [.. files.Resolve(conversations, "conversation-1")];

            // Assert: once, and the refused file is not there.
            TurnStreamPart part = Assert.Single(parts);
            Assert.Equal(TurnStreamPart.File, part.Member);
            FilePayload payload = Assert.IsType<FilePayload>(part.Payload);
            Assert.Equal("chart.png", payload.Name);
            Assert.Equal("Chart", payload.Title);
            Assert.Equal("image/png", payload.MediaType);
            Assert.Equal(48213, payload.Length);
        }

        [Fact]
        public void Resolve_NothingNoted_YieldsNothing()
        {
            // Arrange
            Conversations conversations = new(new InMemoryConversationStore(), blobs: null);

            // Act
            List<TurnStreamPart> parts = [.. new TurnStreamFiles().Resolve(conversations, "conversation-1")];

            // Assert
            Assert.Empty(parts);
        }

        private sealed class StubBlobStore : IBlobStore
        {
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
                throw new NotSupportedException("Listing reads the content and never asks the store.");
            }

            public ValueTask<Uri?> LinkAsync(BlobRef blob, TimeSpan lifetime, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException("Listing never signs a link.");
            }

            public ValueTask DeleteByOwnerAsync(string ownerId, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }
        }
    }
}
