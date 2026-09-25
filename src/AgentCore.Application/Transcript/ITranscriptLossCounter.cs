using Microsoft.Agents.AI;

namespace AgentCore.Application.Transcript
{
    /// <summary>
    /// Counts the store writes of one session that the store did not take, so a later read of the store can tell
    /// them from another session's writes.
    /// </summary>
    internal interface ITranscriptLossCounter
    {
        /// <summary>Reports one dropped store 1 write, and counts it lost. Never throws.</summary>
        /// <param name="turnIndex">The turn the transcript was on when the write was queued.</param>
        /// <param name="appended">
        /// The ids of the rows the write would have appended, or none for a write that changes rows in place. The store
        /// may still hold them: a write can land and still fail to say so.
        /// </param>
        /// <param name="exception">Why the store did not take the write.</param>
        void Dropped(int turnIndex, IReadOnlyList<string> appended, Exception exception);

        /// <summary>
        /// Records that the session is deleting rows itself. A lost append that landed no longer holds them, and is judged
        /// by the rows it still holds. Called in the write queue, after every write queued before the delete. Never throws.
        /// </summary>
        /// <param name="messageIds">The ids of the rows the session deletes.</param>
        void Withdrew(IReadOnlyList<string> messageIds);

        /// <summary>
        /// Judges the losses counted so far against what the store holds, before an edit cuts rows the judgement reads, or
        /// realigns the session's words and ordinals with the store. After that, the store no longer says which of the
        /// session's writes it lost.
        /// </summary>
        /// <param name="session">The session of this conversation.</param>
        /// <param name="cancellationToken">Cancels the reads.</param>
        /// <returns>The verdict, or <see langword="null"/> when nothing was lost or the store cannot be read.</returns>
        ValueTask<TranscriptLossVerdict?> JudgeAsync(AgentSession session, CancellationToken cancellationToken);

        /// <summary>
        /// Records that the edit <paramref name="verdict"/> was judged for is done: the verdict now stands for every loss it judged.
        /// </summary>
        /// <param name="verdict">What <see cref="JudgeAsync"/> returned.</param>
        void Realigned(TranscriptLossVerdict verdict);
    }
}
