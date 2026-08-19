using AgentCore.Application.Runtime;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Ports;

/// <summary>Where store 1 keeps the words of a call.</summary>
public interface ITranscriptStore
{
    /// <summary>Writes a turn's new messages. One turn is one round trip.</summary>
    /// <param name="messages">The rows to write, oldest first.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask AppendAsync(IReadOnlyList<CallMessage> messages, CancellationToken cancellationToken = default);

    /// <summary>Rewrites one already-written message in place, on a barge-in.</summary>
    /// <param name="callId">The call the message belongs to.</param>
    /// <param name="ordinal">The message's position within the call.</param>
    /// <param name="content">What the caller actually heard.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask RewriteAsync(
        string callId, int ordinal, ChatMessage content, CancellationToken cancellationToken = default);
}
