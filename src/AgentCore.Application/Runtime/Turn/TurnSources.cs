using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using AgentCore.Domain.Sources;

namespace AgentCore.Application.Runtime.Turn;

/// <summary>What a turn has cited and not yet attached to a message.</summary>
internal sealed class TurnSources : ISourcePort
{
    private readonly Lock _gate = new();

    private readonly Dictionary<string, List<SourceContent>> _byCallId = new(StringComparer.Ordinal);
    
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
        private readonly TurnSources _sources;
        private readonly string? _previous;
        private bool _closed;

        public OuterCallScope(TurnSources sources, string? previous)
        {
            _sources = sources;
            _previous = previous;
        }

        public void Dispose()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            lock (_sources._gate)
            {
                _sources._outerCallId = _previous;
            }
        }
    }

    /// <inheritdoc/>
    public void Publish(SourceReference source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (_outerCallId is not { } callId)
        {
            return;
        }

        var content = new SourceContent { Source = source, CallId = callId };

        lock (_gate)
        {
            if (!_byCallId.TryGetValue(callId, out var cited))
            {
                cited = [];
                _byCallId[callId] = cited;
            }

            // Two searches in one turn can return the same card. The later publish wins, in the
            // place the earlier one took, so the order the turn cited things in is kept.
            var index = cited.FindIndex(existing =>
                string.Equals(existing.Source.SourceId, source.SourceId, StringComparison.Ordinal));

            if (index >= 0)
            {
                cited[index] = content;
            }
            else
            {
                cited.Add(content);
            }
        }
    }

    /// <summary>Takes what was cited under one outer tool call, in publish order.</summary>
    /// <param name="callId">The call whose sources to take.</param>
    /// <returns>What that call cited, or empty.</returns>
    internal IReadOnlyList<SourceContent> TakeFor(string callId)
    {
        ArgumentNullException.ThrowIfNull(callId);

        lock (_gate)
        {
            return _byCallId.Remove(callId, out var cited) ? cited : [];
        }
    }
}
