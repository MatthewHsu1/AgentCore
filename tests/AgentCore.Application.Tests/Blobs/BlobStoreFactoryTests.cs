using AgentCore.Application.Blobs;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using Xunit;

namespace AgentCore.Application.Tests.Blobs;

/// <summary>
/// The blob vendor seam: <c>providers.blobs</c>, and what a document that names none gets.
/// </summary>
/// <remarks>
/// Unlike the call store, there is no built-in kind. A document that names none gets
/// <see langword="null"/>, and that null is what switches the capture provider off.
/// </remarks>
public sealed class BlobStoreFactoryTests
{
    [Fact]
    public async Task OpenAsync_NoBlobsBlock_GetsNullAndAsksNoAdapter()
    {
        // Arrange
        var configuration = ConfigurationLoader.LoadYaml(Document());
        FakeBlobStoreAdapter adapter = new("s3");

        // Act
        var store = await BlobStoreFactory.OpenAsync(
            configuration, secrets: null, [adapter], TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(store);
        Assert.Equal(0, adapter.Opens);
    }

    [Fact]
    public async Task OpenAsync_TheKind_PicksOneAdapterAndHandsItTheWholeBlock()
    {
        // Arrange
        var configuration = ConfigurationLoader.LoadYaml(
            Document("kind: fake", "endpoint: https://s3.us-east-005.backblazeb2.com", "bucket: spirit", "region: us-east-005"));
        FakeBlobStoreAdapter fake = new("fake");
        FakeBlobStoreAdapter other = new("s3");

        // Act
        var store = await BlobStoreFactory.OpenAsync(
            configuration, secrets: null, [other, fake], TestContext.Current.CancellationToken);

        // Assert
        Assert.Same(fake.Store, store);
        Assert.Equal(0, other.Opens);
        Assert.NotNull(fake.Entry);
        Assert.Equal("https://s3.us-east-005.backblazeb2.com", fake.Entry.Endpoint);
        Assert.Equal("spirit", fake.Entry.Bucket);
        Assert.Equal("us-east-005", fake.Entry.Region);
    }

    [Fact]
    public async Task OpenAsync_AKindNoAdapterServes_FailsTheStartAndPointsAtTheKind()
    {
        // Arrange
        var configuration = ConfigurationLoader.LoadYaml(Document("kind: s3"));
        FakeBlobStoreAdapter adapter = new("fake");

        // Act
        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await BlobStoreFactory.OpenAsync(
                configuration, secrets: null, [adapter], TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("s3", failure.Message, StringComparison.Ordinal);
        Assert.Equal("/providers/blobs/kind", failure.Errors[0].Pointer);
    }

    [Fact]
    public void LoadYaml_AnUnknownKeyUnderBlobs_IsRejectedBySchema()
    {
        // Act & Assert
        Assert.Throws<ConfigurationLoadException>(
            () => ConfigurationLoader.LoadYaml(Document("kind: s3", "path: /tmp")));
    }

    private const string KnowledgeLine = Configuration.ExampleDocument.LastProviderLine;

    /// <summary>Builds the section 8.1 document with one blobs block written into it.</summary>
    private static string Document(params string[] entries)
        => entries.Length == 0
            ? Configuration.ExampleDocument.Yaml
            : Configuration.ExampleDocument.Yaml.Replace(
                KnowledgeLine,
                KnowledgeLine + "\n  blobs:\n" + string.Join("\n", entries.Select(entry => "    " + entry)),
                StringComparison.Ordinal);

    /// <summary>An adapter that opens nothing and records what it was handed.</summary>
    private sealed class FakeBlobStoreAdapter(string kind) : IBlobStoreAdapter
    {
        public string Kind => kind;

        public int Opens { get; private set; }

        public BlobProviderConfiguration? Entry { get; private set; }

        public IBlobStore Store { get; } = new FakeBlobStore();

        public ValueTask<IBlobStore> OpenAsync(
            BlobProviderConfiguration entry,
            ISecretResolverPort? secrets,
            CancellationToken cancellationToken = default)
        {
            Opens++;
            Entry = entry;
            return ValueTask.FromResult(Store);
        }
    }

    private sealed class FakeBlobStore : IBlobStore
    {
        public ValueTask<BlobRef> PutAsync(BlobWrite write, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<BlobRead?> OpenReadAsync(string ownerId, string name, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<BlobRef?> StatAsync(string ownerId, string name, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<Uri?> LinkAsync(BlobRef blob, TimeSpan lifetime, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask DeleteByOwnerAsync(string ownerId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
