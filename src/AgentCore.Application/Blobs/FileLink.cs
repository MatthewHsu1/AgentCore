namespace AgentCore.Application.Blobs;

/// <summary>One file a call's sandbox wrote and the store kept.</summary>
/// <param name="Blob">The file's facts.</param>
/// <param name="Url">Where a browser fetches it from, or <see langword="null"/> when the store has no web door.</param>
public sealed record FileLink(BlobRef Blob, Uri? Url);
