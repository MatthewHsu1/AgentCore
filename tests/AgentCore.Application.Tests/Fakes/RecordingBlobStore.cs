using AgentCore.Application.Blobs;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Tests.Fakes;

/// <summary>An in-memory <see cref="IBlobStore"/> that keeps every write, so a test reads what was stored.</summary>
internal sealed class RecordingBlobStore : IBlobStore
{
    private readonly Dictionary<(string Owner, string Name), (string MediaType, byte[] Bytes)> _blobs = [];

    /// <summary>Gets every blob stored, keyed by owner and name, in no particular order.</summary>
    public IReadOnlyDictionary<(string Owner, string Name), (string MediaType, byte[] Bytes)> Blobs => _blobs;

    public async ValueTask<BlobRef> PutAsync(BlobWrite write, CancellationToken cancellationToken = default)
    {
        using MemoryStream copy = new();
        await write.Content.CopyToAsync(copy, cancellationToken);
        _blobs[(write.OwnerId, write.Name)] = (write.MediaType, copy.ToArray());
        return new BlobRef(write.OwnerId, write.Name, write.MediaType, write.Length);
    }

    public ValueTask<BlobRead?> OpenReadAsync(string ownerId, string name, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_blobs.TryGetValue((ownerId, name), out var blob)
            ? new BlobRead(blob.MediaType, blob.Bytes.Length, new MemoryStream(blob.Bytes))
            : null);

    public ValueTask DeleteByOwnerAsync(string ownerId, CancellationToken cancellationToken = default)
    {
        foreach (var key in _blobs.Keys.Where(key => key.Owner == ownerId).ToList())
        {
            _blobs.Remove(key);
        }

        return ValueTask.CompletedTask;
    }
}
