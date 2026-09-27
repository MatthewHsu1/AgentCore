namespace AgentCore.Application.Runtime
{
    /// <summary>
    /// Where a cut of one running turn waits for the seal, and the turn lock that linearizes the cut against
    /// the turn's commit (design section 3, "Command").
    /// </summary>
    /// <param name="turnLock">The conversation's turn lock.</param>
    /// <param name="cancellationToken">The host's token, which the run's own token is linked to.</param>
    internal sealed class TurnCutSlot(Lock turnLock, CancellationToken cancellationToken) : IDisposable
    {
        private readonly CancellationTokenSource _run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        private TurnCut? _cut;

        private TurnCut? _late;

        private TurnCut? _lateRecut;

        private bool _sealed;

        private bool _committed;

        /// <summary>Gets the turn lock. The commit of the turn runs under it.</summary>
        public Lock Gate { get; } = turnLock;

        /// <summary>Gets the token the run reads. A cut before the seal cancels it.</summary>
        public CancellationToken Token => _run.Token;

        /// <summary>Gets whether a cut reached the turn before its seal.</summary>
        public bool IsCut
        {
            get
            {
                lock (Gate)
                {
                    return _cut is not null;
                }
            }
        }

        /// <summary>Gets whether the turn's words are committed, so a cut from now on amends them.</summary>
        public bool IsCommitted
        {
            get
            {
                lock (Gate)
                {
                    return _committed;
                }
            }
        }

        /// <summary>Records a cut of this turn.</summary>
        /// <param name="cut">What the user saw or heard.</param>
        /// <returns><see langword="false"/> when the turn already committed, so the cut belongs to the committed turn.</returns>
        public bool TryCut(TurnCut cut)
        {
            ArgumentNullException.ThrowIfNull(cut);

            lock (Gate)
            {
                if (_committed)
                {
                    return false;
                }

                if (_sealed)
                {
                    _late = cut;
                    return true;
                }

                _cut = cut;
                _run.Cancel();
                return true;
            }
        }

        /// <summary>Replaces the cut this turn already took.</summary>
        /// <param name="cut">A later account of what the user saw or heard.</param>
        /// <returns>
        /// <see langword="true"/> when the replacement was recorded; <see langword="false"/> when the turn took no cut
        /// before its seal, or already committed, so the recut belongs to the committed turn.
        /// </returns>
        public bool TryRecut(TurnCut cut)
        {
            ArgumentNullException.ThrowIfNull(cut);

            lock (Gate)
            {
                if (_committed || _cut is null)
                {
                    return false;
                }

                if (_sealed)
                {
                    _lateRecut = cut;
                }
                else
                {
                    _cut = cut;
                }

                return true;
            }
        }

        /// <summary>Seals the turn, once: a cut from now on waits for the commit.</summary>
        /// <param name="cut">The cut that stopped the run, or <see langword="null"/> when none did.</param>
        /// <returns><see langword="false"/> when the turn was already sealed.</returns>
        public bool TrySeal(out TurnCut? cut)
        {
            lock (Gate)
            {
                cut = _cut;
                if (_sealed)
                {
                    return false;
                }

                _sealed = true;
                return true;
            }
        }

        /// <summary>Marks the turn committed. The caller holds <see cref="Gate"/> across the commit.</summary>
        /// <param name="recut">The recut that arrived between the seal and the commit, or <see langword="null"/>.</param>
        /// <returns>The cut that arrived between the seal and the commit, or <see langword="null"/>.</returns>
        public TurnCut? Commit(out TurnCut? recut)
        {
            lock (Gate)
            {
                _committed = true;
                recut = _lateRecut;
                return _late;
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _run.Dispose();
        }
    }
}
