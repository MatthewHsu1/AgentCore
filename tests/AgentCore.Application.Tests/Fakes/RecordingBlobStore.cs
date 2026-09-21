using AgentCore.Application.Blobs;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Tests.Fakes
{
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
        {
            return ValueTask.FromResult(_blobs.TryGetValue((ownerId, name), out (string MediaType, byte[] Bytes) blob)
                        ? new BlobRead(blob.MediaType, blob.Bytes.Length, new MemoryStream(blob.Bytes))
                        : null);
        }

        public ValueTask<BlobRef?> StatAsync(string ownerId, string name, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(_blobs.TryGetValue((ownerId, name), out (string MediaType, byte[] Bytes) blob)
                        ? new BlobRef(ownerId, name, blob.MediaType, blob.Bytes.Length)
                        : null);
        }

        /// <summary>A recognisable fake link, so a test can see what was linked and for how long.</summary>
        public ValueTask<Uri?> LinkAsync(BlobRef blob, TimeSpan lifetime, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<Uri?>(new Uri($"https://blobs.test/{blob.OwnerId}/{blob.Name}?ttl={(int)lifetime.TotalSeconds}"));
        }

        public ValueTask DeleteByOwnerAsync(string ownerId, CancellationToken cancellationToken = default)
        {
            foreach ((string Owner, string Name) key in _blobs.Keys.Where(key => key.Owner == ownerId).ToList())
            {
                _ = _blobs.Remove(key);
            }

            return ValueTask.CompletedTask;
        }
    }
}
