using AgentCore.Domain.Audit;
using System.Collections.Concurrent;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;

namespace AgentCore.Application.Sessions.Memory;

/// <summary>
/// The default <see cref="ICallSessions"/>. It holds every session in this process.
/// </summary>
/// <remarks>
/// <para>
/// This does not survive a restart and does not span instances. A process that stops loses every
/// call it was holding, and a second instance behind a load balancer never sees the sessions of the
/// first. A deployment that needs either registers another <see cref="ICallSessions"/> before
/// <c>AddAgentCore</c>, and this steps aside.
/// </para>
/// <para>
/// A session is dropped when it has been untouched for the idle timeout. The voice path does not
/// wait for that — the socket closing is a real end, and it closes the call itself. The timeout is
/// for the text path, where a caller who simply stops replying never reaches a terminal stage and
/// would otherwise be held for the life of the process.
/// </para>
/// </remarks>
public sealed class InMemoryCallSessions : ICallSessions
{
    /// <summary>The idle timeout a host gets when it names none.</summary>
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, Entry> _sessions = new(StringComparer.Ordinal);
    private readonly ICallSessionFactory _factory;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeProvider _time;

    /// <summary>Creates the store.</summary>
    /// <param name="factory">Builds the session of a call that is not held yet.</param>
    /// <param name="idleTimeout">How long an untouched session is kept. It slides on every read.</param>
    /// <param name="timeProvider">The clock the idle timeout is measured on.</param>
    public InMemoryCallSessions(
        ICallSessionFactory factory, TimeSpan idleTimeout, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(idleTimeout, TimeSpan.Zero);

        _factory = factory;
        _idleTimeout = idleTimeout;
        _time = timeProvider;
    }

    /// <summary>Gets how many sessions this holds.</summary>
    public int Count => _sessions.Count;

    /// <inheritdoc />
    public ValueTask<CallSession> OpenAsync(string? callId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var session = _factory.Create(callId);
        _sessions[session.CallId] = new Entry(session, _time.GetUtcNow());
        return ValueTask.FromResult(session);
    }

    /// <inheritdoc />
    public ValueTask<CallSession?> TryGetAsync(string callId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callId);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_sessions.TryGetValue(callId, out var entry))
        {
            return ValueTask.FromResult<CallSession?>(null);
        }

        _sessions.TryUpdate(callId, entry with { Touched = _time.GetUtcNow() }, entry);
        return ValueTask.FromResult<CallSession?>(entry.Session);
    }

    /// <inheritdoc />
    public async ValueTask CloseAsync(string callId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callId);

        // Taken out first so a second caller cannot be handed a session that is already draining,
        // then flushed while this method still holds the only reference to it.
        if (_sessions.TryRemove(callId, out var entry))
        {
            await entry.Session.FlushTranscriptAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Closes every session that has been untouched for the idle timeout.</summary>
    /// <param name="cancellationToken">Cancels the sweep.</param>
    /// <returns>A task that completes once every expired session is closed.</returns>
    /// <exception cref="AggregateException">
    /// One or more sessions could not be ended. The sweep still visited every other session first.
    /// </exception>
    /// <remarks>
    /// This goes through <see cref="CloseAsync"/> and not the dictionary, so an expiring session
    /// hands over its words on the way out exactly as one the host closed by hand.
    /// </remarks>
    public async ValueTask SweepAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = _time.GetUtcNow() - _idleTimeout;
        List<Exception>? faults = null;

        foreach (var (callId, entry) in _sessions.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.Touched > cutoff)
            {
                continue;
            }

            // One session that will not end must not leave every session after it in the
            // dictionary for the life of the process, which is the leak this sweep exists to stop.
            try
            {
                await ExpireAsync(callId, entry.Session, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception fault) when (fault is not OperationCanceledException)
            {
                (faults ??= []).Add(fault);
            }
        }

        if (faults is not null)
        {
            throw new AggregateException(faults);
        }
    }

    /// <summary>Ends one expired call, chain first and session second.</summary>
    /// <remarks>
    /// §11 item 6 makes <c>call.ended</c> the last event of every call, and expiry is a way a call
    /// ends. Nothing else writes it here: the relay closes its own chain from the socket and the
    /// turn loop closes its own from a terminal stage, so a caller who simply stops replying is the
    /// one ending that would otherwise leave a chain with no end. <see cref="CallSession.EndCall(CallEndReason)"/>
    /// is idempotent, so a session that already closed its chain keeps the reason it wrote.
    /// The reason is <see cref="CallEndReason.Faulted"/> because the closed set of §6 holds no
    /// reason for an abandoned call; see the amendment owed on that enum.
    /// </remarks>
    private async ValueTask ExpireAsync(
        string callId, CallSession session, CancellationToken cancellationToken)
    {
        _ = session.EndCall(CallEndReason.Faulted);
        await CloseAsync(callId, cancellationToken).ConfigureAwait(false);
    }

    private sealed record Entry(CallSession Session, DateTimeOffset Touched);
}
