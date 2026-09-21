using Microsoft.Extensions.AI;

namespace AgentCore.Application.Transcript
{
    /// <summary>
    /// A file the reply produced, carried by the message that produced it. This is the only shape a
    /// reader sees: where the file came from is AgentCore's business.
    /// </summary>
    public sealed class FileContent : AIContent
    {
        /// <summary>The file name. It is the name the store keeps it under.</summary>
        public required string Name { get; set; }

        /// <summary>Where the file came from: the workspace-relative path the model published.</summary>
        public required string FileId { get; set; }

        /// <summary>A short label the model gave the file for the person, or <see langword="null"/>.</summary>
        public string? Title { get; set; }

        /// <summary>The IANA media type the store holds the file under.</summary>
        public string MediaType { get; set; } = "application/octet-stream";

        /// <summary>How many bytes the store holds.</summary>
        public long Length { get; set; }

        /// <summary>Whether the store holds the file. A reader links only what is kept.</summary>
        public bool Kept { get; set; }
    }
}
