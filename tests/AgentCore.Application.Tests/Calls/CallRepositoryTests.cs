#pragma warning disable MEAI001

using AgentCore.Application.Blobs;
using AgentCore.Application.Calls;
using AgentCore.Application.Calls.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Tests.Fakes;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Calls;

/// <summary>The one door to a stored call: its row, its words, and its files go together.</summary>
public sealed class CallRepositoryTests
{
    /// <summary>A reply whose references the capture stamped as kept, 3 bytes each.</summary>
    private static ChatMessage ReplyWith(params (string Name, string MediaType)[] kept)
        => new(ChatRole.Assistant, [new TextContent("see"), .. kept.Select(file => Kept(file.Name, file.MediaType))]);

    private static HostedFileContent Kept(string name, string mediaType)
    {
        HostedFileContent reference = new("cfile_" + name) { Name = name };
        SandboxFiles.MarkKept(reference, new BlobRef("call-1", name, mediaType, 3));
        return reference;
    }

    private static HostedFileContent Refused(string name)
    {
        HostedFileContent reference = new("cfile_" + name) { Name = name };
        SandboxFiles.MarkRefused(reference);
        return reference;
    }

    private static async Task<BlobRef> KeepAsync(RecordingBlobStore blobs, string callId, string name, string mediaType)
        => await blobs.PutAsync(new BlobWrite(callId, name, mediaType, new MemoryStream([1, 2, 3]), 3), TestContext.Current.CancellationToken);

    [Fact]
    public async Task DeleteAsync_DeletesTheFilesAndTheRow()
    {
        // Arrange
        InMemoryCallStore store = new();
        RecordingBlobStore blobs = new();
        CallRepository calls = new(store, blobs);
        var token = TestContext.Current.CancellationToken;
        await calls.CreateAsync("call-1", token);
        await KeepAsync(blobs, "call-1", "chart.png", "image/png");
        await KeepAsync(blobs, "call-2", "keep.png", "image/png");

        // Act
        await calls.DeleteAsync("call-1", token);

        // Assert
        Assert.Null(await store.GetAsync("call-1", token));
        Assert.Equal([("call-2", "keep.png")], blobs.Blobs.Keys);
    }

    [Fact]
    public async Task DeleteAsync_NoBlobStore_DeletesTheRowAlone()
    {
        // Arrange
        InMemoryCallStore store = new();
        CallRepository calls = new(store, blobs: null);
        var token = TestContext.Current.CancellationToken;
        await calls.CreateAsync("call-1", token);

        // Act
        await calls.DeleteAsync("call-1", token);

        // Assert
        Assert.Null(await store.GetAsync("call-1", token));
        Assert.False(calls.KeepsFiles);
    }

    [Fact]
    public async Task LinkFilesAsync_LinksFromTheStamp_EachOnce_InFirstSeenOrder_WithoutAskingTheStore()
    {
        // Arrange: the store is empty on purpose. The stamp is the index.
        CountingBlobStore blobs = new();
        CallRepository calls = new(new InMemoryCallStore(), blobs);

        // Act
        var links = await calls.LinkFilesAsync(
            "call-1",
            [
                new ChatMessage(ChatRole.Assistant, [Kept("chart.png", "image/png"), Refused("lost.pdf")]),
                ReplyWith(("rows.csv", "text/csv"), ("chart.png", "image/png")),
            ],
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Collection(
            links,
            link =>
            {
                Assert.Equal(new BlobRef("call-1", "chart.png", "image/png", 3), link.Blob);
                Assert.Equal("https://blobs.test/call-1/chart.png?ttl=900", link.Url?.ToString());
            },
            link => Assert.Equal("rows.csv", link.Blob.Name));
        Assert.Equal(0, blobs.Stats);
    }

    [Fact]
    public async Task LinkFilesAsync_NoBlobStore_LinksNothing()
    {
        // Arrange
        CallRepository calls = new(new InMemoryCallStore(), blobs: null);

        // Act
        var links = await calls.LinkFilesAsync("call-1", [ReplyWith(("chart.png", "image/png"))], TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(links);
    }

    [Fact]
    public async Task LinkFilesAsync_FileInsideAnInterpreterResult_IsFound()
    {
        // Arrange
        CallRepository calls = new(new InMemoryCallStore(), new RecordingBlobStore());
        ChatMessage reply = new(ChatRole.Assistant, [new CodeInterpreterToolResultContent("ci_1") { Outputs = [Kept("chart.png", "image/png")] }]);

        // Act
        var links = await calls.LinkFilesAsync("call-1", [reply], TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("chart.png", Assert.Single(links).Blob.Name);
    }

    /// <summary>Links like the recording store, and counts how often anyone asks it whether a blob exists.</summary>
    private sealed class CountingBlobStore : IBlobStore
    {
        public int Stats { get; private set; }

        public ValueTask<BlobRef> PutAsync(BlobWrite write, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<BlobRead?> OpenReadAsync(string ownerId, string name, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<BlobRef?> StatAsync(string ownerId, string name, CancellationToken cancellationToken = default)
        {
            Stats++;
            return ValueTask.FromResult<BlobRef?>(null);
        }

        public ValueTask<Uri?> LinkAsync(BlobRef blob, TimeSpan lifetime, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<Uri?>(new Uri($"https://blobs.test/{blob.OwnerId}/{blob.Name}?ttl={(int)lifetime.TotalSeconds}"));

        public ValueTask DeleteByOwnerAsync(string ownerId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
