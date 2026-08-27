using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;

namespace AgentCore.Application.Runtime;

/// <summary>What a turn has drawn and not yet attached to a message.</summary>
internal sealed class TurnRenders : IRenderPort
{
    private readonly Lock _gate = new();

    private readonly Dictionary<string, List<RenderContent>> _byCallId = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public void Publish(string name, string renderId, object data, bool transient = false)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(renderId);
        ArgumentNullException.ThrowIfNull(data);

        if (transient || OuterToolCall.Current is not { } callId)
        {
            return;
        }

        var element = JsonSerializer.SerializeToElement(data, data.GetType(), TranscriptJson.Options);
        var content = new RenderContent { Name = name, RenderId = renderId, Data = element };

        lock (_gate)
        {
            if (!_byCallId.TryGetValue(callId, out var drawn))
            {
                drawn = [];
                _byCallId[callId] = drawn;
            }

            var index = drawn.FindIndex(existing => string.Equals(existing.RenderId, renderId, StringComparison.Ordinal));
            if (index >= 0)
            {
                drawn[index] = content;
            }
            else
            {
                drawn.Add(content);
            }
        }
    }

    /// <summary>Takes what was drawn under one outer tool call, in publish order.</summary>
    internal IReadOnlyList<RenderContent> TakeFor(string callId)
    {
        ArgumentNullException.ThrowIfNull(callId);

        lock (_gate)
        {
            return _byCallId.Remove(callId, out var drawn) ? drawn : [];
        }
    }
}
