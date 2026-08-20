using AgentCore.Application.Runtime;

namespace AgentCore.AspNetCore.Sessions;

/// <summary>
/// Finds the <see cref="CallSession"/> of one call again on the next request.
/// </summary>
/// <remarks>
/// <para>
/// A call runs over many turns and a text request carries one turn, so something has to hold the
/// session between two requests. This is that seam. The endpoint reads it and writes it, and knows
/// nothing about where it keeps the session.
/// </para>
/// <para>
/// The methods are asynchronous because a later store talks to a network. A distributed store also
/// has to answer the harder question this interface does not solve: a <see cref="CallSession"/> holds
/// a live transcript and a live stage machine, so a store that spans instances has to carry that
/// state itself and rebuild the session on the far side.
/// </para>
/// </remarks>
public interface ICallSessionStore
{
    /// <summary>Reads the session of one call.</summary>
    /// <param name="sessionId">The id the request named. It is the <see cref="CallSession.CallId"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The session, or <see langword="null"/> when the store holds no such id.</returns>
    ValueTask<CallSession?> TryGetAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Holds one session, so the next request finds it.</summary>
    /// <param name="session">The session the factory just created.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the store holds the session.</returns>
    ValueTask AddAsync(CallSession session, CancellationToken cancellationToken = default);

    /// <summary>Drops one session.</summary>
    /// <param name="sessionId">The id the request named.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true"/> when the store held the id.</returns>
    ValueTask<bool> RemoveAsync(string sessionId, CancellationToken cancellationToken = default);
}
