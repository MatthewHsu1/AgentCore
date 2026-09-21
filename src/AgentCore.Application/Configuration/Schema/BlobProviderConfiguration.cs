namespace AgentCore.Application.Configuration.Schema
{
    /// <summary>
    /// The blob provider: the store that keeps what a vendor's sandbox produced, and where it is.
    /// </summary>
    public sealed record BlobProviderConfiguration
    {
        /// <summary>Gets the vendor, such as <c>s3</c>.</summary>
        public required string Kind { get; init; }

        /// <summary>
        /// Gets the service URL.
        /// <see langword="null"/> when the vendor needs none.
        /// </summary>
        public string? Endpoint { get; init; }

        /// <summary>Gets the bucket every blob is written into, or <see langword="null"/> when the vendor needs none.</summary>
        public string? Bucket { get; init; }

        /// <summary>
        /// Gets the region the request signature names, or <see langword="null"/> to read it off the
        /// endpoint host.
        /// </summary>
        public string? Region { get; init; }
    }
}
