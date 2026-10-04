namespace AgentCore.Application.Hooks.Notices
{
    /// <summary><c>file.publish</c> stored a file.</summary>
    /// <param name="Scope">Where and when it happened.</param>
    /// <param name="Name">The file's name.</param>
    /// <param name="Length">The file's length in bytes.</param>
    /// <param name="MediaType">The file's media type.</param>
    /// <param name="BlobKey"><c>ownerId/name</c> of the stored blob.</param>
    public sealed record FilePublished(
        HookScope Scope,
        string Name,
        long Length,
        string MediaType,
        string BlobKey)
        : HookNotice(Scope);
}
