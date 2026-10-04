using AgentCore.Application.Transcript;
using AgentCore.Domain.Sources;

namespace AgentCore.Application.Runtime.Turn
{
    /// <summary>What a turn has cited and not yet attached to a message.</summary>
    internal sealed class TurnSources : TurnAttachments<SourceContent>
    {
        /// <summary>Cites one source under the outermost tool call of the citing flow, or drops it outside one.</summary>
        /// <param name="source">Where the answer came from.</param>
        /// <param name="callId">The outermost tool call the citing flow runs inside, or <see langword="null"/>.</param>
        public void Publish(SourceReference source, string? callId)
        {
            ArgumentNullException.ThrowIfNull(source);

            if (callId is null)
            {
                return;
            }

            SourceContent content = new() { Source = source, CallId = callId };

            // Two searches in one turn can return the same card.
            Attach(callId, content, existing =>
                string.Equals(existing.Source.SourceId, source.SourceId, StringComparison.Ordinal));
        }
    }
}
