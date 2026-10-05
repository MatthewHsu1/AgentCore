namespace AgentCore.Application.Runtime.Turn.Lifecycle
{
    /// <summary>The words a turn records when it does not answer, and how long its completion may take.</summary>
    public static class TurnFailureReasons
    {
        /// <summary>The reason a turn that produced no text reports.</summary>
        public const string EmptyReply = "the run returned an empty reply, so the turn spoke the fallback.";

        /// <summary>The reason a turn that lost its tool budget reports, before the message of the fault.</summary>
        public const string ToolFailure = "a tool failed four times in a row, so the turn spoke the fallback.";

        /// <summary>
        /// The reason a turn whose run faulted outside every tool reports, before the message of the fault: a model
        /// endpoint that did not answer, or a graph that matched no edge. No tool let the fault out, so this is never
        /// <see cref="ToolFailure"/>'s wording.
        /// </summary>
        public const string RunFault = "the turn's run faulted, so it spoke the fallback.";

        /// <summary>The failure the turn records when the completion work passes its deadline.</summary>
        internal const string ExtractionTimedOut = "the turn completion passed its deadline.";

        /// <summary>What the log records when the moderation endpoint runs out of time.</summary>
        internal const string ModerationTimedOut = "the moderation endpoint passed its deadline.";

        /// <summary>What the log records when the moderation endpoint throws.</summary>
        internal const string ModerationFaulted = "the moderation endpoint threw.";

        /// <summary>How long the work after the reply may take before it is abandoned.</summary>
        internal static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(5);
    }
}
