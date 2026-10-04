using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime.Turn.Lifecycle
{
    /// <summary>One turn that has started: its index, and its reply as it arrives.</summary>
    public sealed class TurnRun : IAsyncDisposable
    {
        private const int Unread = 0;

        private const int Reading = 1;

        private const int Disposed = 2;

        private readonly IAsyncEnumerable<ChatResponseUpdate> _updates;

        private readonly Action? _abandon;

        private readonly Lock _gate = new();

        private IAsyncEnumerator<ChatResponseUpdate>? _reader;

        private int _state;

        /// <summary>Creates a run over a reply the caller already has.</summary>
        /// <param name="turnIndex">The index of the turn, which <c>Cut</c> names.</param>
        /// <param name="updates">The reply, one update at a time.</param>
        /// <exception cref="ArgumentNullException"><paramref name="updates"/> is <see langword="null"/>.</exception>
        public TurnRun(int turnIndex, IAsyncEnumerable<ChatResponseUpdate> updates)
            : this(turnIndex, updates, abandon: null)
        {
        }

        /// <param name="turnIndex">The index of the turn.</param>
        /// <param name="updates">The reply. Its enumeration holds the conversation, and ends the turn in a <c>finally</c>.</param>
        /// <param name="abandon">Ends a turn whose reply was never read, or <see langword="null"/> when there is nothing to end.</param>
        internal TurnRun(int turnIndex, IAsyncEnumerable<ChatResponseUpdate> updates, Action? abandon)
        {
            ArgumentNullException.ThrowIfNull(updates);

            TurnIndex = turnIndex;
            _updates = updates;
            _abandon = abandon;
        }

        /// <summary>Gets the index of the turn, which <c>Cut</c> names.</summary>
        public int TurnIndex { get; }

        /// <summary>
        /// Gets the reply, one update at a time. Every update carries content. Enumerate it once: the turn
        /// commits only as the enumeration runs and ends.
        /// </summary>
        public IAsyncEnumerable<ChatResponseUpdate> Updates => ReadAsync();

        /// <summary>Frees the conversation this run holds. Safe to call more than once.</summary>
        public async ValueTask DisposeAsync()
        {
            int before;
            IAsyncEnumerator<ChatResponseUpdate>? reader;

            lock (_gate)
            {
                before = _state;
                reader = _reader;
                _state = Disposed;
            }

            if (before == Unread)
            {
                _abandon?.Invoke();
            }
            else if (before == Reading && reader is not null)
            {
                await reader.DisposeAsync().ConfigureAwait(false);
            }
        }

        private async IAsyncEnumerable<ChatResponseUpdate> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            IAsyncEnumerator<ChatResponseUpdate> reader;

            lock (_gate)
            {
                if (_state != Unread)
                {
                    throw new InvalidOperationException(_state == Disposed
                        ? $"The run of turn {TurnIndex} was disposed, so its reply can no longer be read."
                        : $"The reply of turn {TurnIndex} is already being read.");
                }

                reader = _updates.GetAsyncEnumerator(cancellationToken);
                _reader = reader;
                _state = Reading;
            }

            try
            {
                while (await reader.MoveNextAsync().ConfigureAwait(false))
                {
                    yield return reader.Current;
                }
            }
            finally
            {
                await reader.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
