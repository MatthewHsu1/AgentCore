namespace AgentCore.Application.Blobs
{
    /// <summary>
    /// The one shape a blob owner id may take. An owner id is the first segment of a key, never a path.
    /// </summary>
    public static class BlobOwner
    {
        /// <summary>Says whether an owner id is safe to use as the first segment of a key.</summary>
        /// <param name="ownerId">The conversation id or other owner the caller wrote.</param>
        /// <returns><see langword="true"/> when the id is not blank and holds no slash.</returns>
        public static bool IsSafe(string? ownerId)
        {
            return !string.IsNullOrWhiteSpace(ownerId) && !ownerId.Contains('/', StringComparison.Ordinal);
        }
    }
}
