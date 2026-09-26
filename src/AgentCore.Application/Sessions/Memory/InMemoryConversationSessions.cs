using System.Collections.Concurrent;
using AgentCore.Application.Conversation;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Sessions.Memory
{
    /// <summary>
    /// The default <see cref="IConversationSessions"/>. It holds every session in this process.
    /// </summary>
    public sealed class InMemoryConversationSessions : IConversationSessions, IDisposable
    {
        /// <summary>The idle timeout a host gets when it names none.</summary>
        public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(10);

        // The longest due time ITimer.Change accepts.
        private static readonly TimeSpan MaxIdleTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

        private readonly ConcurrentDictionary<string, SessionSlot> _slots = new(StringComparer.Ordinal);

        private readonly IReadOnlyDictionary<string, IConversationSessionFactory> _factories;

        private readonly TimeSpan _idleTimeout;

        private readonly TimeProvider _time;

        private volatile bool _disposed;

        /// <summary>Creates the store.</summary>
        /// <param name="factories">Builds the session of a conversation that is not held yet, keyed by entry name.</param>
        /// <param name="idleTimeout">
        /// How long an untouched session is kept. It slides on every read, and restarts when a running turn ends.
        /// At most about 49 days.
        /// </param>
        /// <param name="timeProvider">The clock the idle timers run on.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="idleTimeout"/> is not positive, or is longer than a timer can wait.
        /// </exception>
        public InMemoryConversationSessions(
            IReadOnlyDictionary<string, IConversationSessionFactory> factories, TimeSpan idleTimeout, TimeProvider timeProvider)
        {
            ArgumentNullException.ThrowIfNull(factories);
            ArgumentNullException.ThrowIfNull(timeProvider);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(idleTimeout, TimeSpan.Zero);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(idleTimeout, MaxIdleTimeout);

            _factories = factories;
            _idleTimeout = idleTimeout;
            _time = timeProvider;
        }

        /// <summary>Gets how many live sessions this holds. A session still being built, or already closing, is not counted.</summary>
        public int Count => _slots.Values.Count(static slot => slot.TryGetHeld(out HeldSession? held) && !held.IsEnded);

        /// <inheritdoc />
        /// <exception cref="ObjectDisposedException">This store was disposed.</exception>
        /// <exception cref="ArgumentException"><paramref name="entry"/> is empty, or names no declared entry.</exception>
        public async ValueTask<ConversationSession> GetOrOpenAsync(
            string entry, string? conversationId, ConversationSessionState? state, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(entry);
            IConversationSessionFactory factory = FactoryFor(entry);

            // Minted here rather than by the factory, so a new conversation reserves its id like any other.
            string id = string.IsNullOrWhiteSpace(conversationId) ? Guid.NewGuid().ToString("N") : conversationId;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed, this);

                if (!_slots.TryGetValue(id, out SessionSlot? slot))
                {
                    SessionSlot reserved = new(entry);
                    if (_slots.TryAdd(id, reserved))
                    {
                        return Build(id, reserved, factory, state);
                    }

                    continue;
                }

                // A closing session's folder is about to be deleted, whichever entry held it.
                if (slot.TryGetHeld(out HeldSession? closing) && closing.IsEnded)
                {
                    await slot.Released.WaitAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!string.Equals(slot.Entry, entry, StringComparison.Ordinal))
                {
                    throw new ConversationInUseException(id, slot.Entry, entry);
                }

                HeldSession held = await slot.Opened.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (held.TryTouch())
                {
                    return held.Session;
                }

                await slot.Released.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        /// <exception cref="ArgumentException"><paramref name="entry"/> is empty.</exception>
        public ValueTask<ConversationSession?> TryGetAsync(
            string entry, string conversationId, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(entry);
            ArgumentNullException.ThrowIfNull(conversationId);
            cancellationToken.ThrowIfCancellationRequested();

            return ValueTask.FromResult(
                _slots.TryGetValue(conversationId, out SessionSlot? slot)
                && string.Equals(slot.Entry, entry, StringComparison.Ordinal)
                && slot.TryGetHeld(out HeldSession? held)
                && held.TryTouch()
                    ? held.Session
                    : null);
        }

        /// <inheritdoc />
        /// <exception cref="ArgumentException"><paramref name="entry"/> is empty.</exception>
        public async ValueTask CloseAsync(string entry, string conversationId, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(entry);
            ArgumentNullException.ThrowIfNull(conversationId);

            if (!_slots.TryGetValue(conversationId, out SessionSlot? slot)
                || !string.Equals(slot.Entry, entry, StringComparison.Ordinal)
                || !slot.TryGetHeld(out HeldSession? held))
            {
                return;
            }

            if (held.TryEnd())
            {
                await TearDownAsync(conversationId, slot, held).ConfigureAwait(false);
            }
            else
            {
                await slot.Released.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Sweeps <paramref name="root"/> for a folder a crashed process left behind. See
        /// <see cref="WorkspaceRootSweeper.Sweep"/> for what counts as one, and why. Meant to run once, at
        /// boot, before this owner has opened any session of its own; a folder this owner already holds by the
        /// time this runs is skipped regardless of its stamp, so a caller that runs it later never sweeps its
        /// own live session.
        /// </summary>
        /// <param name="root">The workspace root a host bound.</param>
        /// <param name="logger">Where a failed list or delete is logged, at Warning. May be <see langword="null"/>.</param>
        /// <exception cref="ArgumentException"><paramref name="root"/> is empty.</exception>
        internal void SweepWorkspaceRoot(string root, ILogger? logger)
        {
            new WorkspaceRootSweeper(_idleTimeout, _time).Sweep(root, id => _slots.ContainsKey(id), logger);
        }

        /// <summary>Stops every idle timer. A build or a close already running finishes, and wakes its waiters.</summary>
        public void Dispose()
        {
            _disposed = true;

            foreach (SessionSlot slot in _slots.Values)
            {
                if (slot.TryGetHeld(out HeldSession? held))
                {
                    held.Dispose();
                }
            }
        }

        /// <summary>
        /// The one close routine every way out runs: flush, dispose (background children, then shells), delete
        /// the workspace folder. A store that throws on flush must not leave the shells running with nothing
        /// left to hold a reference to them, and a dispose that throws must not leave the folder undeleted.
        /// </summary>
        private static async ValueTask CloseSessionAsync(ConversationSession session)
        {
            try
            {
                try
                {
                    await session.FlushTranscriptAsync().ConfigureAwait(false);
                }
                finally
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                session.Lifetime.DeleteWorkspace();
            }
        }

        /// <summary>Builds the session of an id this caller reserved, and hands it to every caller waiting on the slot.</summary>
        private ConversationSession Build(
            string conversationId, SessionSlot slot, IConversationSessionFactory factory, ConversationSessionState? state)
        {
            HeldSession held;
            try
            {
                ConversationSession session = factory.Create(conversationId, state);
                held = new(session, _idleTimeout, _time, expired => Expire(conversationId, slot, expired));
            }
            catch (Exception fault)
            {
                _ = _slots.TryRemove(KeyValuePair.Create(conversationId, slot));
                slot.Fail(fault);
                throw;
            }

            slot.Open(held);

            // A Dispose that ran before the slot was opened did not see this timer.
            if (_disposed)
            {
                held.Dispose();
            }

            return held.Session;
        }

        private IConversationSessionFactory FactoryFor(string entry)
        {
            return _factories.TryGetValue(entry, out IConversationSessionFactory? factory)
                ? factory
                : throw new ArgumentException(
                    $"The entry '{entry}' is not declared. Valid entries: {string.Join(", ", _factories.Keys)}.",
                    nameof(entry));
        }

        /// <summary>Starts the unload of one expired session. Its idle timer ended it, so no other close runs.</summary>
        private void Expire(string conversationId, SessionSlot slot, HeldSession held)
        {
            _ = ExpireAsync(conversationId, slot, held);
        }

        /// <summary>Unloads one expired conversation. It writes no end event; the conversation stays open.</summary>
        private async Task ExpireAsync(string conversationId, SessionSlot slot, HeldSession held)
        {
            // Nothing awaits an expiry, so nothing may escape it.
#pragma warning disable CA1031
            try
            {
                await TearDownAsync(conversationId, slot, held).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                SessionOwnerLog.SessionExpiryFailed(held.Session.Logger, conversationId, exception);
            }
#pragma warning restore CA1031
        }

        /// <summary>
        /// Tears down a session whose close this caller began, then gives up its slot. The slot stays taken until
        /// the teardown is done, so an open of the id waits instead of racing the folder delete.
        /// </summary>
        private async Task TearDownAsync(string conversationId, SessionSlot slot, HeldSession held)
        {
            try
            {
                await CloseSessionAsync(held.Session).ConfigureAwait(false);
            }
            finally
            {
                _ = _slots.TryRemove(KeyValuePair.Create(conversationId, slot));
                slot.Release();
            }
        }
    }
}
