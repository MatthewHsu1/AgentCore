namespace AgentCore.Application.Blobs;

/// <summary>What the capture recorded about one kept sandbox file, as read back off the reply.</summary>
/// <param name="Name">The name the model gave it, which is the name it is stored under.</param>
/// <param name="MediaType">The IANA media type the store holds it as.</param>
/// <param name="Length">How many bytes the store holds.</param>
public sealed record SandboxFileFacts(string Name, string MediaType, long Length);
