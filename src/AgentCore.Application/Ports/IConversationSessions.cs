using AgentCore.Application.Runtime;

namespace AgentCore.Application.Ports;

/// <summary>
/// Owns the session of a conversation for as long as the conversation needs it.
/// </summary>
/// <remarks>
/// A conversation runs over many turns and one request carries one turn, so something has to hold the
/// session in between. This is that seam, and it owns the whole life of the session rather than
/// only the holding: a caller asks for the session of a conversation and never builds one itself.
/// </remarks>
public interface IConversationSessions
{
    /// <summary>Opens the session of one conversation.</summary>
    /// <param name="conversationId">The id the host gives the conversation, or <see langword="null"/> to be given one.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The session, ready for its first turn.</returns>
    ValueTask<ConversationSession> OpenAsync(string? conversationId, CancellationToken cancellationToken = default);

    /// <summary>Reads the session of one conversation, and marks the conversation as still live.</summary>
    /// <param name="conversationId">The id the request named. It is the <see cref="ConversationSession.ConversationId"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The session, or <see langword="null"/> when this holds no such conversation.</returns>
    /// <remarks>
    /// A read is the only signal an implementation gets that a conversation is still being had, so an
    /// implementation that expires an idle session must restart that session's clock here. A caller
    /// that already holds the session for the whole conversation — the relay socket does — must therefore
    /// still read it back on each turn, or its own conversation is the one that expires.
    /// </remarks>
    ValueTask<ConversationSession?> TryGetAsync(string conversationId, CancellationToken cancellationToken = default);

    /// <summary>Ends one conversation: waits for the words it still owes store 1, then drops it.</summary>
    /// <param name="conversationId">The id of the conversation that ended.</param>
    /// <param name="cancellationToken">Cancels the close.</param>
    /// <returns>A task that completes once the session is gone and its writes are done.</returns>
    /// <remarks>
    /// The order is the contract. A turn queues its rows and speaks, so a conversation can end with its last
    /// turn still in flight, and the session is the only thing that can wait for those writes — once
    /// it is dropped nothing can, and the durable record loses the turn the caller just had with no
    /// error anywhere to say so. Every way a session ends comes through here, expiry included, so
    /// no path can be written that skips the wait. An implementation must also dispose the session:
    /// it owns processes (a conversation's shell: executors) that a dropped reference would leak.
    /// </remarks>
    ValueTask CloseAsync(string conversationId, CancellationToken cancellationToken = default);
}
