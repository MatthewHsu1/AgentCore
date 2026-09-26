namespace AgentCore.Application.Transcript
{
    /// <summary>
    /// The store 1 writes of one session that the store did not take, since the session last caught up with it.
    /// </summary>
    /// <remarks>
    /// A catch-up clears the losses counted up to the mark it read before its store read, not every loss: a write the
    /// store drops while a catch-up is in flight stays counted for the next one.
    /// </remarks>
    internal sealed class TranscriptLosses
    {
        private readonly Lock _gate = new();

        // Both guarded by _gate.
        private readonly List<TranscriptLoss> _lost = [];

        private int _counted;

        /// <summary>Gets whether a write was lost since the session last caught up.</summary>
        internal bool Any
        {
            get
            {
                lock (_gate)
                {
                    return _lost.Count > 0;
                }
            }
        }

        /// <summary>Gets whether the store refused a turn since the session last caught up.</summary>
        internal bool Refused
        {
            get
            {
                lock (_gate)
                {
                    return _lost.Exists(static loss => loss.Refused);
                }
            }
        }

        /// <summary>Counts one write the store did not take.</summary>
        /// <param name="appended">The ids of the rows it would have appended.</param>
        /// <param name="refused">Whether the store refused it because another session saved its turn first.</param>
        internal void Lose(IReadOnlyList<string> appended, bool refused)
        {
            lock (_gate)
            {
                _lost.Add(new TranscriptLoss(++_counted, appended, refused));
            }
        }

        /// <summary>Records that the session deleted rows itself, so no loss counts them missing.</summary>
        /// <param name="messageIds">The ids of the rows deleted.</param>
        internal void Withdrew(IReadOnlyList<string> messageIds)
        {
            lock (_gate)
            {
                for (int index = 0; index < _lost.Count; index++)
                {
                    TranscriptLoss loss = _lost[index];
                    if (loss.Held.Any(messageIds.Contains))
                    {
                        _lost[index] = loss with { Held = [.. loss.Held.Where(id => !messageIds.Contains(id))] };
                    }
                }
            }
        }

        /// <summary>Reads the losses as they stand.</summary>
        /// <returns>
        /// The mark to hand <see cref="CaughtUp"/> once the session took what the store held, and every loss since the
        /// last catch-up, oldest first.
        /// </returns>
        internal (int Mark, IReadOnlyList<TranscriptLoss> Since) Read()
        {
            lock (_gate)
            {
                return (_counted, [.. _lost]);
            }
        }

        /// <summary>Records that the session took what the store held when <paramref name="mark"/> was read.</summary>
        /// <param name="mark">What <see cref="Read"/> returned before that store read.</param>
        internal void CaughtUp(int mark)
        {
            lock (_gate)
            {
                _ = _lost.RemoveAll(loss => loss.Sequence <= mark);
            }
        }

        /// <summary>
        /// Records that an edit judged by <paramref name="verdict"/> is done: it cut rows the losses up to the verdict's
        /// mark were judged by, or realigned the session's words and ordinals with the store. The verdict stands for those
        /// losses from then on. A refusal and an overtaking stay counted: only a catch-up that takes the stored state
        /// clears them.
        /// </summary>
        internal void Realigned(TranscriptLossVerdict verdict)
        {
            lock (_gate)
            {
                _ = _lost.RemoveAll(loss => loss.Sequence <= verdict.Mark && !loss.Refused && !loss.Overtaken);
                if (verdict.Overtaken)
                {
                    _lost.Add(new TranscriptLoss(++_counted, [], Refused: false, Overtaken: true));
                }
            }
        }
    }
}
