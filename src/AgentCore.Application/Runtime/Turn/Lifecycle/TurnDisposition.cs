using Microsoft.Agents.AI;
using AgentCore.Application.Runtime.Agents;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Runtime.Turn.Lifecycle
{
    /// <summary>What moderation decided about the caller's words.</summary>
    internal enum ModerationOutcome
    {
        /// <summary>The endpoint answered and flagged nothing.</summary>
        Clean,

        /// <summary>The endpoint answered and flagged the words, so the model never ran.</summary>
        Flagged,

        /// <summary>The endpoint did not answer. Moderation fails open, so the turn ran unchecked.</summary>
        Unavailable,
    }

    /// <summary>Why <see cref="FallbackAgent"/> spoke instead of the model.</summary>
    internal enum FallbackCause
    {
        /// <summary>It did not. The model answered.</summary>
        None,

        /// <summary>Something below the layer threw.</summary>
        Faulted,

        /// <summary>The run returned quietly with no text.</summary>
        EmptyReply,
    }

    /// <summary>What the pipeline layers report back to <see cref="ConversationSession"/> about a turn.</summary>
    /// <param name="Moderation">
    /// What the endpoint decided, or that it could not, or <see langword="null"/> when the pipeline
    /// carries no <see cref="ModerationAgent"/> at all because the host moderates nothing.
    /// </param>
    /// <param name="FlaggedCategories">
    /// The moderation endpoint's order, unmodified, or <see langword="null"/> when nothing was flagged.
    /// </param>
    /// <param name="Fallback">Why the fallback layer spoke, or <see cref="FallbackCause.None"/>.</param>
    /// <param name="FallbackFault">
    /// The exception the fallback layer caught, or <see langword="null"/>. Kept live and not flattened to a
    /// message here: the turn's <c>tool.failed</c> row needs the message, and the span and the Error log
    /// each need a different cut of it (type only, and the full object), so the decision of which text goes
    /// where stays with the turn loop, not with this layer.
    /// </param>
    /// <param name="ModerationReason">
    /// Why the endpoint did not answer, in the words the turn loop logs, or <see langword="null"/>. It
    /// is set only when <paramref name="Moderation"/> is <see cref="ModerationOutcome.Unavailable"/>.
    /// </param>
    internal sealed record TurnDisposition(
        ModerationOutcome? Moderation,
        string? FlaggedCategories,
        FallbackCause Fallback,
        Exception? FallbackFault,
        string? ModerationReason)
    {
        /// <summary>Gets whether a BeforeTurn hook blocked the turn before moderation and the model.</summary>
        public bool Blocked { get; init; }
    }
}
