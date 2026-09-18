using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;

namespace AgentCore.Application.Runtime.Turn;

/// <summary>What a turn has drawn and not yet attached to a message.</summary>
internal sealed class TurnRenders : TurnAttachments<RenderContent>, IRenderPort
{
    /// <inheritdoc/>
    public void Publish(string name, string renderId, object data, bool transient = false)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(renderId);
        ArgumentNullException.ThrowIfNull(data);

        if (transient || OuterCallId is not { } callId)
        {
            return;
        }

        var element = JsonSerializer.SerializeToElement(data, data.GetType(), TranscriptJson.Options);
        var content = new RenderContent { Name = name, RenderId = renderId, Data = element };

        Attach(callId, content, existing => string.Equals(existing.RenderId, renderId, StringComparison.Ordinal));
    }
}
