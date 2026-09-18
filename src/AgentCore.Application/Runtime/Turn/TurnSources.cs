using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using AgentCore.Domain.Sources;

namespace AgentCore.Application.Runtime.Turn;

/// <summary>What a turn has cited and not yet attached to a message.</summary>
internal sealed class TurnSources : TurnAttachments<SourceContent>, ISourcePort
{
    /// <inheritdoc/>
    public void Publish(SourceReference source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (OuterCallId is not { } callId)
        {
            return;
        }

        var content = new SourceContent { Source = source, CallId = callId };

        // Two searches in one turn can return the same card.
        Attach(callId, content, existing =>
            string.Equals(existing.Source.SourceId, source.SourceId, StringComparison.Ordinal));
    }
}
