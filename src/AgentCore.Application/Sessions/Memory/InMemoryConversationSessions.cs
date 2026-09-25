using System.Collections.Concurrent;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Domain.Audit;

namespace AgentCore.Application.Sessions.Memory
{
    /// <summary>
    /// The default <see cref="IConversationSessions"/>. It holds every session in this process.
    /// </summary>
    public sealed class InMemoryConversationSessions : IConversationSessions, IDisposable
    {
        /// <summary>The idle timeout a host gets when it names none.</summary>
        public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(30);

        // The longest due time ITimer.Change accepts.
        private static readonly TimeSpan MaxIdleTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

        private readonly ConcurrentDictionary<string, HeldSession> _sessions = new(StringComparer.Ordinal);

        private readonly IConversationSessionFactory _factory;

        private readonly TimeSpan _idleTimeout;

        private readonly TimeProvider _time;

        private volatile bool _disposed;

        /// <summary>Creates the store.</summary>
        /// <param name="factory">Builds the session of a conversation that is not held yet.</param>
        /// <param name="idleTimeout">
        /// How long an untouched session is kept. It slides on every read, and restarts when a running turn ends.
        /// At most about 49 days.
        /// </param>
        /// <param name="timeProvider">The clock the idle timers run on.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="idleTimeout"/> is not positive, or is longer than a timer can wait.
        /// </exception>
        public InMemoryConversationSessions(
            IConversationSessionFactory factory, TimeSpan idleTimeout, TimeProvider timeProvider)
        {
            ArgumentNullException.ThrowIfNull(factory);
            ArgumentNullException.ThrowIfNull(timeProvider);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(idleTimeout, TimeSpan.Zero);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(idleTimeout, MaxIdleTimeout);

            _factory = factory;
            _idleTimeout = idleTimeout;
            _time = timeProvider;
        }

        /// <summary>Gets how many sessions this holds.</summary>
        public int Count => _sessions.Count;

        /// <inheritdoc />
        /// <exception cref="ObjectDisposedException">This store was disposed.</exception>
        public ValueTask<ConversationSession> OpenAsync(string? conversationId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);

            ConversationSession session = _factory.Create(conversationId);
            HeldSession held = new(session, _idleTimeout, _time, Expire);
            HeldSession? replaced = null;

            _ = _sessions.AddOrUpdate(session.ConversationId, held, (_, old) =>
            {
                replaced = old;
                return held;
            });

            replaced?.Dispose();

            // A Dispose that ran between the check above and the add did not see this timer.
            if (_disposed)
            {
                held.Dispose();
            }

            return ValueTask.FromResult(session);
        }

        /// <inheritdoc />
        public ValueTask<ConversationSession?> TryGetAsync(string conversationId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(conversationId);
            cancellationToken.ThrowIfCancellationRequested();

            return ValueTask.FromResult(
                _sessions.TryGetValue(conversationId, out HeldSession? held) && held.TryTouch() ? held.Session : null);
        }

        /// <inheritdoc />
        public async ValueTask CloseAsync(string conversationId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(conversationId);

            // Taken out first so a second caller cannot be handed a session that is already draining,
            // then flushed while this method still holds the only reference to it.
            if (_sessions.TryRemove(conversationId, out HeldSession? held))
            {
                held.End();
                await CloseSessionAsync(held.Session).ConfigureAwait(false);
            }
        }

        /// <summary>Stops every idle timer.</summary>
        public void Dispose()
        {
            _disposed = true;

            foreach (HeldSession held in _sessions.Values)
            {
                held.Dispose();
            }
        }

        private static async ValueTask CloseSessionAsync(ConversationSession session)
        {
            // A store that throws on flush must not leave the session's shells running with
            // nothing left to hold a reference to them.
            try
            {
                await session.FlushTranscriptAsync().ConfigureAwait(false);
            }
            finally
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }

        /// <summary>Takes one expired session out, unless a newer session replaced it or the host closed it first.</summary>
        private void Expire(HeldSession held)
        {
            if (_sessions.TryRemove(KeyValuePair.Create(held.Session.ConversationId, held)))
            {
                _ = ExpireAsync(held.Session);
            }
        }

        /// <summary>Ends one expired conversation, chain first and session second.</summary>
        private static async Task ExpireAsync(ConversationSession session)
        {
            // Nothing awaits an expiry, so nothing may escape it. The session is already out of the dictionary,
            // so a chain that fails to end must not stop the close that releases its shells.
#pragma warning disable CA1031
            try
            {
                _ = session.EndConversation(ConversationEndReason.Faulted);
            }
            catch (Exception exception)
            {
                Log.SessionExpiryFailed(session.Logger, session.ConversationId, exception);
            }

            try
            {
                await CloseSessionAsync(session).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Log.SessionExpiryFailed(session.Logger, session.ConversationId, exception);
            }
#pragma warning restore CA1031
        }
    }
}
