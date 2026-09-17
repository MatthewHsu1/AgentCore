#pragma warning disable MEAI001

using AgentCore.Application.Blobs;
using AgentCore.Application.Calls;
using AgentCore.Application.Calls.Memory;
using AgentCore.Application.Ports;
using AgentCore.AspNetCore.Endpoints;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Endpoints;

/// <summary>Which sandbox files reach the browser as a part, and with what link.</summary>
public sealed class TurnStreamFilesTests
{
    private static ChatResponseUpdate UpdateWith(params AIContent[] contents)
        => new(ChatRole.Assistant, contents);

    /// <summary>A reference the capture provider stamped as kept, as it does before the stream ends.</summary>
    private static HostedFileContent Kept(string name, string mediaType, long length)
    {
        HostedFileContent reference = new("cfile_" + name) { Name = name };
        SandboxFiles.MarkKept(reference, new BlobRef("call-1", name, mediaType, length));
        return reference;
    }

    [Fact]
    public async Task ResolveAsync_NotedFileTheCaptureKept_YieldsOnePartWithTheLink()
    {
        // Arrange: the same reference passes twice, as a re-yielded update would; a refused one passes too.
        CallRepository calls = new(new InMemoryCallStore(), new StubBlobStore());
        TurnStreamFiles files = new();
        var chart = Kept("chart.png", "image/png", 48213);
        HostedFileContent refused = new("cfile_2") { Name = "refused.png" };
        SandboxFiles.MarkRefused(refused);
        files.Note(UpdateWith(new TextContent("see"), chart));
        files.Note(UpdateWith(chart));
        files.Note(UpdateWith(refused));

        // Act
        var parts = await files.ResolveAsync(calls, "call-1", TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        // Assert: once, linked, and the refused file is not there.
        var part = Assert.Single(parts);
        Assert.Equal("chart.png", part.Payload.Name);
        Assert.Equal("image/png", part.Payload.MediaType);
        Assert.Equal(48213, part.Payload.Length);
        Assert.Equal("https://blobs.test/call-1/chart.png?ttl=900", part.Payload.Url);
    }

    [Fact]
    public async Task ResolveAsync_StoreWithNoWebDoor_YieldsThePartWithoutAUrl()
    {
        // Arrange
        CallRepository calls = new(new InMemoryCallStore(), new StubBlobStore { Links = false });
        TurnStreamFiles files = new();
        files.Note(UpdateWith(Kept("rows.csv", "text/csv", 8)));

        // Act
        var parts = await files.ResolveAsync(calls, "call-1", TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(Assert.Single(parts).Payload.Url);
    }

    [Fact]
    public async Task ResolveAsync_NothingNoted_YieldsNothing()
    {
        // Arrange
        CallRepository calls = new(new InMemoryCallStore(), blobs: null);

        // Act
        var parts = await new TurnStreamFiles().ResolveAsync(calls, "call-1", TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(parts);
    }

    private sealed class StubBlobStore : IBlobStore
    {
        public bool Links { get; init; } = true;

        public ValueTask<BlobRef> PutAsync(BlobWrite write, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<BlobRead?> OpenReadAsync(string ownerId, string name, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<BlobRef?> StatAsync(string ownerId, string name, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("The read path links from the stamp and never asks the store.");

        public ValueTask<Uri?> LinkAsync(BlobRef blob, TimeSpan lifetime, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Links ? new Uri($"https://blobs.test/{blob.OwnerId}/{blob.Name}?ttl={(int)lifetime.TotalSeconds}") : null);

        public ValueTask DeleteByOwnerAsync(string ownerId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
