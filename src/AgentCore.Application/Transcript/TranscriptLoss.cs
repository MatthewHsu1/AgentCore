namespace AgentCore.Application.Transcript
{
    /// <summary>One store 1 write of a session that the store did not take, or did not say it took.</summary>
    /// <param name="Sequence">Where the loss falls among every loss the session counted, from 1.</param>
    /// <param name="Appended">The ids of the rows the write appends, or none for a write that changes rows in place.</param>
    /// <param name="Refused">Whether the store refused the write because another session saved its turn first.</param>
    /// <param name="Overtaken">
    /// Whether this marks a judgement, not a write: when an edit cut the rows the losses before this one were judged by,
    /// the store held another session's writes in their place.
    /// </param>
    internal sealed record TranscriptLoss(int Sequence, IReadOnlyList<string> Appended, bool Refused, bool Overtaken = false)
    {
        /// <summary>
        /// Gets the ids of <see cref="Appended"/> the session has not deleted itself since. Had the append landed, the
        /// store would hold exactly these of its rows.
        /// </summary>
        public IReadOnlyList<string> Held { get; init; } = Appended;
    }
}
