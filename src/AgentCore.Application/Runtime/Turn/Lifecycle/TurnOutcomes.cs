using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Runtime.Cut;

namespace AgentCore.Application.Runtime.Turn.Lifecycle
{
    /// <summary>Reads the outcome of one sealed turn.</summary>
    internal static class TurnOutcomes
    {
        /// <summary>Reads the outcome of one sealed turn.</summary>
        /// <param name="outcome">What the turn said and why.</param>
        /// <param name="disposition">What the turn layers reported, or <see langword="null"/>.</param>
        /// <param name="cut">The cut the turn was sealed with, or <see langword="null"/>.</param>
        /// <param name="escaped">Whether a fault reached the seal past every layer.</param>
        /// <returns>The closed outcome the <see cref="TurnCompleted"/> notice carries.</returns>
        internal static TurnOutcome Of(ReplyOutcome outcome, TurnDisposition? disposition, TurnCut? cut, bool escaped)
        {
            if (disposition?.Moderation is ModerationOutcome.Flagged || disposition?.Blocked == true)
            {
                return TurnOutcome.Blocked;
            }

            // The seal's own stop cut (host cancel, or a reader that let go) carries neither field.
            if (cut is { ShownText: null, Played: null })
            {
                return TurnOutcome.Cancelled;
            }

            if (outcome.Fault is null)
            {
                return outcome.Failure is null ? TurnOutcome.Answered : TurnOutcome.Empty;
            }

            return escaped ? TurnOutcome.Faulted : TurnOutcome.Fallback;
        }
    }
}
