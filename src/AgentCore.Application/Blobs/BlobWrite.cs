namespace AgentCore.Application.Blobs;

/// <summary>One blob to store.</summary>
/// <param name="OwnerId">Who writes it. The conversation id today.</param>
/// <param name="Name">The name it is stored under. Must pass <see cref="BlobName.IsSafe"/>.</param>
/// <param name="MediaType">The IANA media type, such as <c>image/png</c>.</param>
/// <param name="Content">The bytes. Read once, from its current position. The caller disposes it.</param>
/// <param name="Length">How many bytes <paramref name="Content"/> holds.</param>
public sealed record BlobWrite(
    string OwnerId,
    string Name,
    string MediaType,
    Stream Content,
    long Length);
