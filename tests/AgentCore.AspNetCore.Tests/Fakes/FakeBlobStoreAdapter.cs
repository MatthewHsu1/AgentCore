using AgentCore.Application.Blobs;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;

namespace AgentCore.AspNetCore.Tests.Fakes;

/// <summary>A <c>providers.blobs</c> vendor that opens one in-memory store, so a test reads what a turn published.</summary>
internal sealed class FakeBlobStoreAdapter(string kind = "fake") : IBlobStoreAdapter
{
    public string Kind => kind;

    /// <summary>Gets the one store every open hands over.</summary>
    public FakeBlobStore Store { get; } = new();

    public ValueTask<IBlobStore> OpenAsync(
        BlobProviderConfiguration entry, ISecretResolverPort? secrets, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IBlobStore>(Store);
}

/// <summary>An in-memory <see cref="IBlobStore"/> whose links name the owner and the blob, so a test can read them off a URL.</summary>
internal sealed class FakeBlobStore : IBlobStore
{
    private readonly Dictionary<(string Owner, string Name), (string MediaType, byte[] Bytes)> _blobs = [];

    /// <summary>Gets every blob stored, keyed by owner and name.</summary>
    public IReadOnlyDictionary<(string Owner, string Name), (string MediaType, byte[] Bytes)> Blobs => _blobs;

    public async ValueTask<BlobRef> PutAsync(BlobWrite write, CancellationToken cancellationToken = default)
    {
        using MemoryStream copy = new();
        await write.Content.CopyToAsync(copy, cancellationToken);
        lock (_blobs)
        {
            _blobs[(write.OwnerId, write.Name)] = (write.MediaType, copy.ToArray());
        }

        return new BlobRef(write.OwnerId, write.Name, write.MediaType, write.Length);
    }

    public ValueTask<BlobRead?> OpenReadAsync(string ownerId, string name, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_blobs.TryGetValue((ownerId, name), out var blob)
            ? new BlobRead(blob.MediaType, blob.Bytes.Length, new MemoryStream(blob.Bytes))
            : null);

    public ValueTask<BlobRef?> StatAsync(string ownerId, string name, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_blobs.TryGetValue((ownerId, name), out var blob)
            ? new BlobRef(ownerId, name, blob.MediaType, blob.Bytes.Length)
            : null);

    public ValueTask<Uri?> LinkAsync(BlobRef blob, TimeSpan lifetime, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<Uri?>(new Uri($"https://blobs.test/{blob.OwnerId}/{blob.Name}?ttl={(int)lifetime.TotalSeconds}"));

    public ValueTask DeleteByOwnerAsync(string ownerId, CancellationToken cancellationToken = default)
    {
        lock (_blobs)
        {
            foreach (var key in _blobs.Keys.Where(key => key.Owner == ownerId).ToList())
            {
                _blobs.Remove(key);
            }
        }

        return ValueTask.CompletedTask;
    }
}
