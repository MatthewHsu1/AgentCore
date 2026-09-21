namespace AgentCore.Application.Blobs
{
    /// <summary>One stored blob, apart from its bytes.</summary>
    /// <param name="OwnerId">Who wrote it.</param>
    /// <param name="Name">The name it is stored under.</param>
    /// <param name="MediaType">The IANA media type, such as <c>image/png</c>.</param>
    /// <param name="Length">How many bytes it holds.</param>
    public sealed record BlobRef(
        string OwnerId,
        string Name,
        string MediaType,
        long Length);
}
