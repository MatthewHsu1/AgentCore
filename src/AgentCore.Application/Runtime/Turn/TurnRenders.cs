using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;

namespace AgentCore.Application.Runtime.Turn;

/// <summary>What a turn has drawn and not yet attached to a message.</summary>
internal sealed class TurnRenders : IRenderPort
{
    private readonly Lock _gate = new();

    private readonly Dictionary<string, List<RenderContent>> _byCallId = new(StringComparer.Ordinal);
    
    // The outermost tool call publishes file under, pushed by the invoking client around the
    // outermost invocation. Nested calls keep filing under the outer call; outside a turn there
    // is none, and publishes are discarded, exactly as before.
    private string? _outerCallId;

    /// <summary>Opens one outermost tool call as the key publishes file under.</summary>
    /// <param name="callId">The id of the outermost tool call now running.</param>
    /// <returns>The scope. Disposing it puts back the key that was open before.</returns>
    internal IDisposable BeginOuterCall(string callId)
    {
        ArgumentNullException.ThrowIfNull(callId);

        string? previous;
        lock (_gate)
        {
            previous = _outerCallId;
            _outerCallId = callId;
        }

        return new OuterCallScope(this, previous);
    }

    /// <summary>One open outer call. Disposing it puts back what was open before.</summary>
    private sealed class OuterCallScope : IDisposable
    {
        private readonly TurnRenders _renders;
        private readonly string? _previous;
        private bool _closed;

        public OuterCallScope(TurnRenders renders, string? previous)
        {
            _renders = renders;
            _previous = previous;
        }

        public void Dispose()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            lock (_renders._gate)
            {
                _renders._outerCallId = _previous;
            }
        }
    }

    /// <inheritdoc/>
    public void Publish(string name, string renderId, object data, bool transient = false)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(renderId);
        ArgumentNullException.ThrowIfNull(data);

        if (transient || _outerCallId is not { } callId)
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
