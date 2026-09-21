using AgentCore.Application.Blobs;

namespace AgentCore.Application.Ports
{
    /// <summary>
    /// Durable bytes an owner wrote, keyed by owner and name.
    /// </summary>
    public interface IBlobStore
    {
        /// <summary>Writes one blob. The same owner and name replaces the earlier blob.</summary>
        /// <param name="write">What to store. The stream is read once, from its current position.</param>
        /// <param name="cancellationToken">Cancels the write.</param>
        /// <returns>What was stored.</returns>
        ValueTask<BlobRef> PutAsync(BlobWrite write, CancellationToken cancellationToken = default);

        /// <summary>Opens one blob for reading.</summary>
        /// <param name="ownerId">Who wrote it.</param>
        /// <param name="name">The name it was written under.</param>
        /// <param name="cancellationToken">Cancels the open.</param>
        /// <returns>The blob, or <see langword="null"/> when no such blob exists. The caller disposes it.</returns>
        ValueTask<BlobRead?> OpenReadAsync(string ownerId, string name, CancellationToken cancellationToken = default);

        /// <summary>Reads one blob's facts without its bytes.</summary>
        /// <param name="ownerId">Who wrote it.</param>
        /// <param name="name">The name it was written under.</param>
        /// <param name="cancellationToken">Cancels the read.</param>
        /// <returns>The blob, or <see langword="null"/> when no such blob exists.</returns>
        ValueTask<BlobRef?> StatAsync(string ownerId, string name, CancellationToken cancellationToken = default);

        /// <summary>Makes a URL a browser can fetch the blob from, with no other credential.</summary>
        /// <param name="blob">The blob to link. Its media type decides whether the browser shows it or saves it.</param>
        /// <param name="lifetime">How long the URL works. <see cref="BlobLink.Lifetime"/> is the usual value.</param>
        /// <param name="cancellationToken">Cancels the signing.</param>
        /// <returns>The URL, or <see langword="null"/> when this store has no web door of its own.</returns>
        ValueTask<Uri?> LinkAsync(BlobRef blob, TimeSpan lifetime, CancellationToken cancellationToken = default);

        /// <summary>Deletes every blob an owner wrote. Nothing to delete is not an error.</summary>
        /// <param name="ownerId">Whose blobs to delete.</param>
        /// <param name="cancellationToken">Cancels the delete.</param>
        ValueTask DeleteByOwnerAsync(string ownerId, CancellationToken cancellationToken = default);
    }
}
