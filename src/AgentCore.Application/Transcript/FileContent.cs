using Microsoft.Extensions.AI;

namespace AgentCore.Application.Transcript;

/// <summary>
/// A file the reply produced, carried by the message that produced it. This is the only shape a
/// reader sees: where the file came from, and how the vendor reported it, is AgentCore's business.
/// </summary>
public sealed class FileContent : AIContent
{
    /// <summary>The name the model gave the file. It is the name the store keeps it under.</summary>
    public required string Name { get; set; }

    /// <summary>The vendor's id for the file. What the capture downloads by.</summary>
    public required string FileId { get; set; }

    /// <summary>The vendor's container the file sits in, or <see langword="null"/> when the vendor has none.</summary>
    public string? Scope { get; set; }

    /// <summary>The IANA media type. The vendor's guess until the capture keeps the file, then the store's.</summary>
    public string MediaType { get; set; } = "application/octet-stream";

    /// <summary>How many bytes the store holds. Zero until the capture keeps the file.</summary>
    public long Length { get; set; }

    /// <summary>
    /// Whether the store holds the file. <see langword="false"/> until the capture keeps it, and
    /// for good when the policy refused it, the download failed, or no capture ran at all.
    /// </summary>
    public bool Kept { get; set; }
}
