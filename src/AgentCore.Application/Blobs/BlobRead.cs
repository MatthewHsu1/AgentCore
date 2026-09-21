namespace AgentCore.Application.Blobs
{
    /// <summary>One stored blob, opened for reading. Dispose it to release the stream.</summary>
    /// <param name="MediaType">The IANA media type, such as <c>image/png</c>.</param>
    /// <param name="Length">How many bytes <paramref name="Content"/> holds.</param>
    /// <param name="Content">The bytes, positioned at the start.</param>
    public sealed record BlobRead(
        string MediaType,
        long Length,
        Stream Content) : IAsyncDisposable
    {
        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            return Content.DisposeAsync();
        }
    }
}
