using AgentCore.Application.Runtime;

namespace AgentCore.AspNetCore.Sessions;

/// <summary>
/// Owns the session of a call for as long as the call needs it.
/// </summary>
/// <remarks>
/// A call runs over many turns and one request carries one turn, so something has to hold the
/// session in between. This is that seam, and it owns the whole life of the session rather than
/// only the holding: a caller asks for the session of a call and never builds one itself.
/// </remarks>
public interface ICallSessions
{
    /// <summary>Opens the session of one call.</summary>
    /// <param name="callId">The id the host gives the call, or <see langword="null"/> to be given one.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The session, ready for its first turn.</returns>
    ValueTask<CallSession> OpenAsync(string? callId, CancellationToken cancellationToken = default);

    /// <summary>Reads the session of one call, and marks the call as still live.</summary>
    /// <param name="callId">The id the request named. It is the <see cref="CallSession.CallId"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The session, or <see langword="null"/> when this holds no such call.</returns>
    /// <remarks>
    /// A read is the only signal an implementation gets that a call is still being had, so an
    /// implementation that expires an idle session must restart that session's clock here. A caller
    /// that already holds the session for the whole call — the relay socket does — must therefore
    /// still read it back on each turn, or its own call is the one that expires.
    /// </remarks>
    ValueTask<CallSession?> TryGetAsync(string callId, CancellationToken cancellationToken = default);

    /// <summary>Ends one call: waits for the words it still owes store 1, then drops it.</summary>
    /// <param name="callId">The id of the call that ended.</param>
    /// <param name="cancellationToken">Cancels the close.</param>
    /// <returns>A task that completes once the session is gone and its writes are done.</returns>
    /// <remarks>
    /// The order is the contract. A turn queues its rows and speaks, so a call can end with its last
    /// turn still in flight, and the session is the only thing that can wait for those writes — once
    /// it is dropped nothing can, and the durable record loses the turn the caller just had with no
    /// error anywhere to say so. Every way a session ends comes through here, expiry included, so
    /// no path can be written that skips the wait.
    /// </remarks>
    ValueTask CloseAsync(string callId, CancellationToken cancellationToken = default);
}
