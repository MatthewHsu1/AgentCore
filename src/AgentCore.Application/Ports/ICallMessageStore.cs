using AgentCore.Application.Runtime;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Ports;

/// <summary>Where store 1 keeps the words of a call.</summary>
/// <remarks>
/// <para>
/// Four operations are the whole surface, and only three of them are on the call path. An
/// implementation is free to fail: <see cref="Runtime.AgentCoreChatHistoryProvider"/> catches, logs,
/// and lets the call continue, because the live history stays in the session and a lost row costs
/// the durable record of a turn and nothing else.
/// </para>
/// <para>
/// The read is deliberately absent. A history read is served from the live session, so a store that
/// is unreachable mid-call can never answer a turn with no memory of the call.
/// </para>
/// </remarks>
internal interface ICallMessageStore
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
