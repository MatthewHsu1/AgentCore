using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Transcript;

/// <summary>Something the caller was shown, carried by the message that produced it.</summary>
public sealed class RenderContent : AIContent
{
    /// <summary>The renderer the host looks up.</summary>
    public required string Name { get; set; }

    /// <summary>
    /// Names the thing shown. Publishing the same id again within a turn replaces it.
    /// </summary>
    public required string RenderId { get; set; }

    /// <summary>The payload that renderer reads. Its shape belongs to the renderer.</summary>
    public required JsonElement Data { get; set; }
}
