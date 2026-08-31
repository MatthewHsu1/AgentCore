using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using AgentCore.Domain.Sources;

namespace AgentCore.Application.Runtime;

/// <summary>What a turn has cited and not yet attached to a message.</summary>
internal sealed class TurnSources : ISourcePort
{
    private readonly Lock _gate = new();

    private readonly Dictionary<string, List<SourceContent>> _byCallId = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public void Publish(SourceReference source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (OuterToolCall.Current is not { } callId)
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
