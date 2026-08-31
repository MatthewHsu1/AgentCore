using AgentCore.Domain.Sources;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Transcript;

/// <summary>Where an answer came from, carried by the message that produced it.</summary>
public sealed class SourceContent : AIContent
{
    /// <summary>The source this content carries.</summary>
    public required SourceReference Source { get; set; }

    /// <summary>
    /// The id of the tool call this source was cited under.
    /// </summary>
    public required string CallId { get; set; }
}
