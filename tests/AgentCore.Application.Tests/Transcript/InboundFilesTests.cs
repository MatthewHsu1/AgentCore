using AgentCore.Application.Blobs;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Transcript;

/// <summary>
/// Inbound caller files: filed to the blob store under the conversation, rehydrated for the model.
/// </summary>
public sealed class InboundFilesTests
{
    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3, 4];

    private sealed class MemoryBlobStore : IBlobStore
    {
        public readonly Dictionary<(string Owner, string Name), (string MediaType, byte[] Bytes)> Blobs = [];

        public ValueTask<BlobRef> PutAsync(BlobWrite write, CancellationToken cancellationToken = default)
        {
            using MemoryStream copy = new();
            write.Content.CopyTo(copy);
            byte[] bytes = copy.ToArray();
            lock (Blobs)
            {
                Blobs[(write.OwnerId, write.Name)] = (write.MediaType, bytes);
            }

            return ValueTask.FromResult(new BlobRef(write.OwnerId, write.Name, write.MediaType, write.Length));
        }

        public ValueTask<BlobRead?> OpenReadAsync(string ownerId, string name, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(
                Blobs.TryGetValue((ownerId, name), out (string MediaType, byte[] Bytes) blob)
                    ? new BlobRead(blob.MediaType, blob.Bytes.Length, new MemoryStream(blob.Bytes, writable: false))
                    : null);
        }

        public ValueTask<BlobRef?> StatAsync(string ownerId, string name, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(
                Blobs.TryGetValue((ownerId, name), out (string MediaType, byte[] Bytes) blob)
                    ? new BlobRef(ownerId, name, blob.MediaType, blob.Bytes.Length)
                    : null);
        }

        public ValueTask<Uri?> LinkAsync(BlobRef blob, TimeSpan lifetime, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<Uri?>(new Uri($"https://blobs.test/{blob.OwnerId}/{blob.Name}"));
        }

        public ValueTask DeleteByOwnerAsync(string ownerId, CancellationToken cancellationToken = default)
        {
            lock (Blobs)
            {
                foreach ((string Owner, string Name) key in Blobs.Keys.Where(key => key.Owner == ownerId).ToList())
                {
                    _ = Blobs.Remove(key);
                }
            }

            return ValueTask.CompletedTask;
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FileAsync_WritesDataContentToTheStoreAndKeepsAReference()
    {
        MemoryBlobStore blobs = new();
        ChatMessage message = new(
            ChatRole.User,
            [new TextContent("look"), new DataContent(Png, "image/png") { Name = "photo.png" }]);

        ChatMessage filed = await InboundFiles.FileAsync(blobs, "conv-1", message, Ct);

        (string Owner, string Name) key = Assert.Single(blobs.Blobs.Keys);
        Assert.Equal("conv-1", key.Owner);
        Assert.StartsWith("in-", key.Name, StringComparison.Ordinal);
        Assert.EndsWith(".png", key.Name, StringComparison.Ordinal);
        Assert.Equal(Png, blobs.Blobs[key].Bytes);
        FileContent file = Assert.IsType<FileContent>(filed.Contents[1]);
        Assert.Equal(key.Name, file.Name);
        Assert.Equal("upload:photo.png", file.FileId);
        Assert.Equal("photo.png", file.Title);
        Assert.Equal("image/png", file.MediaType);
        Assert.Equal(Png.Length, file.Length);
        Assert.True(file.Kept);
        Assert.DoesNotContain(filed.Contents, content => content is DataContent);
    }

    [Fact]
    public async Task FileAsync_WithoutDataContent_ReturnsTheSameInstance()
    {
        MemoryBlobStore blobs = new();
        ChatMessage message = new(ChatRole.User, "hello");

        Assert.Same(message, await InboundFiles.FileAsync(blobs, "conv-1", message, Ct));
        Assert.Empty(blobs.Blobs);
    }

    [Fact]
    public async Task RehydrateAsync_ReturnsDataContentAndLeavesTheStoredMessageAlone()
    {
        MemoryBlobStore blobs = new();
        ChatMessage message = new(
            ChatRole.User,
            [new TextContent("look"), new DataContent(Png, "image/png") { Name = "photo.png" }]);
        ChatMessage filed = await InboundFiles.FileAsync(blobs, "conv-1", message, Ct);

        List<ChatMessage> hydrated = await InboundFiles.RehydrateAsync(blobs, "conv-1", [filed], Ct);

        ChatMessage back = Assert.Single(hydrated);
        DataContent data = Assert.IsType<DataContent>(back.Contents[1]);
        Assert.Equal(Png, data.Data.ToArray());
        Assert.Equal("image/png", data.MediaType);
        Assert.Equal("photo.png", data.Name);
        Assert.IsType<FileContent>(filed.Contents[1]);
    }

    [Fact]
    public async Task RehydrateAsync_MissingBlob_SaysSoInText()
    {
        MemoryBlobStore blobs = new();
        ChatMessage filed = new(
            ChatRole.User,
            [new FileContent { Name = "in-gone", FileId = "upload:photo.png", Title = "photo.png", MediaType = "image/png", Length = 4, Kept = true }]);

        ChatMessage back = Assert.Single(await InboundFiles.RehydrateAsync(blobs, "conv-1", [filed], Ct));

        Assert.Equal("[the attachment 'photo.png' is no longer stored]", Assert.IsType<TextContent>(back.Contents[0]).Text);
    }

    [Fact]
    public void Is_TrueOnlyForKeptUploadFiles()
    {
        Assert.True(InboundFiles.Is(new FileContent { Name = "in-a.png", FileId = "upload:photo.png", Kept = true }));
        Assert.False(InboundFiles.Is(new FileContent { Name = "report.csv", FileId = "file.publish", Kept = true }));
        Assert.False(InboundFiles.Is(new FileContent { Name = "in-a.png", FileId = "upload:photo.png", Kept = false }));
        Assert.Equal("upload:", InboundFiles.FileIdPrefix);
    }
}
