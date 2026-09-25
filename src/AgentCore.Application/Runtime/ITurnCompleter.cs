using AgentCore.Application.Transcript;

namespace AgentCore.Application.Runtime
{
    /// <summary>
    /// The conversation's side of one turn's seal, which <see cref="ConversationTurnAgent"/> calls in a fixed
    /// order: <see cref="CompleteAsync"/>, then the provider's commit under the turn lock, then
    /// <see cref="RefusedAsync"/>, then <see cref="Committed"/> under the turn lock and <see cref="FinishAsync"/>.
    /// A turn the store refused stops after <see cref="RefusedAsync"/>.
    /// </summary>
    internal interface ITurnCompleter
    {
        /// <summary>Reports that the turn's edit withdrew earlier turns, before the run starts.</summary>
        /// <param name="withdrawn">The turns the edit took.</param>
        void Superseded(WithdrawnTurns withdrawn);

        /// <summary>
        /// Decides the turn after its run: the tool-result writers, the extractor, the clock, the counters and the
        /// stage advance.
        /// </summary>
        /// <param name="sealing">The turn as the layer saw it end.</param>
        /// <param name="fault">The fault the run threw, or <see langword="null"/>.</param>
        /// <returns><paramref name="sealing"/> completed with the state and the line the record holds.</returns>
        ValueTask<TurnCommit> CompleteAsync(TurnCommit sealing, Exception? fault);

        /// <summary>
        /// Waits for the store's answer to the commit, and closes the turn when the store refused it because another
        /// session of the conversation saved the same turn first.
        /// </summary>
        /// <param name="refused">Completes with <see langword="true"/> when the store refused the commit.</param>
        /// <returns><see langword="true"/> when the turn was refused, and publishes nothing.</returns>
        ValueTask<bool> RefusedAsync(Task<bool> refused);

        /// <summary>Raises the turn's events once its words are written. Runs under the turn lock.</summary>
        /// <param name="ids">What the commit wrote the user's message and the last message under, or <see langword="null"/>.</param>
        /// <param name="spoken">The words the turn's rows say (<see cref="TurnWords.Spoken"/>), which the turn's hashes prove.</param>
        /// <param name="late">A cut that arrived after the seal and before the commit, or <see langword="null"/>.</param>
        /// <param name="recut">A recut of the turn's own cut that arrived after the seal and before the commit, or <see langword="null"/>.</param>
        void Committed((string UserMessageId, string? ReplyMessageId)? ids, string spoken, TurnCut? late, TurnCut? recut);

        /// <summary>Closes the turn after the lock: its telemetry, and the conversation when it is over.</summary>
        ValueTask FinishAsync();
    }
}
