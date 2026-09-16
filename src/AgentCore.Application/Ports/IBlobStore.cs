using AgentCore.Application.Blobs;

namespace AgentCore.Application.Ports;

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

    /// <summary>Deletes every blob an owner wrote. Nothing to delete is not an error.</summary>
    /// <param name="ownerId">Whose blobs to delete.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    ValueTask DeleteByOwnerAsync(string ownerId, CancellationToken cancellationToken = default);
}
