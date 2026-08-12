using System.Collections.Concurrent;
using AgentCore.Application.Runtime;

namespace AgentCore.AspNetCore.Sessions;

/// <summary>
/// The default <see cref="ICallSessionStore"/>. It holds every session in this process.
/// </summary>
/// <remarks>
/// <para>
/// This store does not survive a restart, and it does not span instances. A process that stops loses
/// every call it was holding, and a second instance behind a load balancer never sees the sessions of
/// the first one. A deployment that needs either of those replaces this store: register another
/// <see cref="ICallSessionStore"/> before <c>AddAgentCore</c>, and the default steps aside.
/// </para>
/// <para>
/// This store also evicts nothing. It is right for the text path of one process and for a test, and a
/// long-running deployment needs a store that expires an abandoned call.
/// </para>
/// </remarks>
public sealed class InMemoryCallSessionStore : ICallSessionStore
{
    private readonly ConcurrentDictionary<string, CallSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>Gets how many sessions this store holds.</summary>
    public int Count => _sessions.Count;

    /// <inheritdoc />
    public ValueTask<CallSession?> TryGetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(_sessions.TryGetValue(sessionId, out var session) ? session : null);
    }

    /// <inheritdoc />
    public ValueTask AddAsync(CallSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();

        _sessions[session.CallId] = session;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<bool> RemoveAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(_sessions.TryRemove(sessionId, out _));
    }
}
