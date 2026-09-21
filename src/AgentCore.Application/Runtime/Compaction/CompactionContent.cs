using AgentCore.Application.Runtime.Turn;

namespace AgentCore.Application.Runtime.Compaction
{
    /// <summary>
    /// Tells the turn's own caller that a compaction pass is starting or has finished.
    /// </summary>
    public sealed class CompactionContent : NoticeContent
    {
        /// <summary>The value <see cref="Phase"/> carries while the pass is running.</summary>
        public const string StartPhase = "start";

        /// <summary>The value <see cref="Phase"/> carries once the pass has finished.</summary>
        public const string EndPhase = "end";

        /// <summary>Creates a notice for one phase of a compaction pass.</summary>
        /// <param name="phase"><see cref="StartPhase"/> or <see cref="EndPhase"/>.</param>
        /// <param name="outcome">How the pass went. Only set on <see cref="EndPhase"/>.</param>
        public CompactionContent(string phase, string? outcome = null)
        {
            ArgumentException.ThrowIfNullOrEmpty(phase);
            Phase = phase;
            Outcome = outcome;
        }

        /// <summary>Gets whether this notice opens or closes a compaction pass.</summary>
        public string Phase { get; }

        /// <summary>Gets how the pass went: <c>compacted</c>, <c>unchanged</c>, or <c>failed</c>.
        /// Always <see langword="null"/> on <see cref="StartPhase"/>.</summary>
        public string? Outcome { get; }
    }
}
