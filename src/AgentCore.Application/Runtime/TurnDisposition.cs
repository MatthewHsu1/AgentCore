using Microsoft.Agents.AI;

namespace AgentCore.Application.Runtime;

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

    /// <summary>Something below the layer threw. Section 8.7, row six.</summary>
    Faulted,

    /// <summary>The run returned quietly with no text. Section 8.7, last row.</summary>
    EmptyReply,
}

/// <summary>What the pipeline layers report back to <see cref="CallSession"/> about a turn.</summary>
/// <param name="Moderation">
/// What the endpoint decided, or that it could not, or <see langword="null"/> when the pipeline
/// carries no <see cref="ModerationAgent"/> at all because the host moderates nothing.
/// </param>
/// <param name="FlaggedCategories">
/// The moderation endpoint's order, unmodified, or <see langword="null"/> when nothing was flagged.
/// </param>
/// <param name="Fallback">Why the fallback layer spoke, or <see cref="FallbackCause.None"/>.</param>
/// <param name="FallbackReason">
/// The message of the fault the fallback layer caught, or <see langword="null"/>. The turn's
/// <c>tool.failed</c> row carries this text, so it is the exception's message and not its type name.
/// </param>
/// <param name="ModerationReason">
/// Why the endpoint did not answer, in the words the turn loop logs, or <see langword="null"/>. It
/// is set only when <paramref name="Moderation"/> is <see cref="ModerationOutcome.Unavailable"/>.
/// </param>
/// <remarks>
/// <para>
/// Carried on <see cref="AgentResponse.AdditionalProperties"/> when buffered, and on a leading
/// <see cref="AgentResponseUpdate"/> when streaming — update-level properties do not survive
/// streaming coalescing, so the streaming reader must take it off the update, not the folded run.
/// </para>
/// <para>
/// It is attached and read with <see cref="AdditionalPropertiesExtensions"/>, which keys on the full
/// type name, so nothing else in the pipeline can collide with it.
/// </para>
/// </remarks>
internal sealed record TurnDisposition(
    ModerationOutcome? Moderation,
    string? FlaggedCategories,
    FallbackCause Fallback,
    string? FallbackReason,
    string? ModerationReason);
