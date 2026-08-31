using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Transcript;

/// <summary>How a stored message is encoded and decoded. Every store and the state bag agree on it.</summary>
public static class TranscriptJson
{
    /// <summary>The shared options. Every store and the state bag serialise a <c>ChatMessage</c> with it.</summary>
    public static JsonSerializerOptions Options { get; } = Build();

    private static JsonSerializerOptions Build()
    {
        // AIJsonUtilities.DefaultOptions ships the polymorphic converters, so a tool call, a tool
        // result, text, and usage all round-trip with no code of ours. It is read-only, though, and
        // AddAIContentType throws on a read-only instance, so copy first.
        JsonSerializerOptions options = new(AIJsonUtilities.DefaultOptions)
        {
            // jsonb keeps no key order, so the $type discriminator does not come back first.
            // Without this every read of a stored message throws.
            AllowOutOfOrderMetadataProperties = true,
        };

        options.AddAIContentType<RenderContent>(RenderContentTypeId);
        options.AddAIContentType<SourceContent>(SourceContentTypeId);
        options.MakeReadOnly();

        return options;
    }

    /// <summary>The discriminator a stored RenderContent is written with. It is a wire format.</summary>
    private const string RenderContentTypeId = "agentcore.render";

    /// <summary>The discriminator a stored SourceContent is written with. It is a wire format.</summary>
    private const string SourceContentTypeId = "agentcore.source";
}
