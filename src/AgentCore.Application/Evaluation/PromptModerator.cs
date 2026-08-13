using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace AgentCore.Application.Evaluation;

/// <summary>
/// Reads which moderation categories flagged what the caller said.
/// </summary>
/// <remarks>
/// <para>
/// The agent moderates the caller's words BEFORE the model runs, and refuses the turn when the
/// endpoint flags them. That is a decision of the owner, taken on 2026-08-13, and it departs from
/// section 11 item 11, which asked for the agent's reply to be moderated and recorded instead. The
/// reply side is not built: <see cref="Domain.Audit.AuditEventKind.ReplyFlagged"/> still has no
/// producer.
/// </para>
/// <para>
/// D13 makes <see cref="IEvaluator"/> the moderation port and refuses a second one, so this type is
/// not a port. It is the one place that turns an <see cref="EvaluationResult"/> into the fact the
/// audit chain records, and it exists so that <see cref="Runtime.CallSession"/> holds no registry
/// key, no metric name, and no decode.
/// </para>
/// <para>
/// D13 also says moderation checks EVERY turn, because the endpoint is free at any volume and counts
/// against no usage limit: "Sampling buys nothing when the call is free." Nothing here reads
/// <see cref="EvaluationSampler"/>, and nothing should.
/// </para>
/// <para>
/// <b>Nothing here catches.</b> A timeout, a log line, and the decision to answer anyway all belong
/// to <see cref="Runtime.CallSession"/>, which owns the turn and the clock. This type calls the
/// evaluator and reads the answer.
/// </para>
/// </remarks>
public sealed class PromptModerator
{
    /// <summary>The name the composition root registers the moderation evaluator under.</summary>
    public const string ModerationEvaluatorName = "moderation";

    private static readonly IReadOnlyList<string> NothingFlagged = [];

    private readonly IEvaluator _evaluator;

    /// <summary>Builds a moderator over one evaluator.</summary>
    /// <param name="evaluator">The evaluator that reaches the moderation endpoint.</param>
    /// <exception cref="ArgumentNullException">The evaluator is <see langword="null"/>.</exception>
    public PromptModerator(IEvaluator evaluator)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        _evaluator = evaluator;
    }

    /// <summary>Builds a moderator over the evaluator the host registered, or none.</summary>
    /// <param name="registry">The registry the composition root filled.</param>
    /// <returns>
    /// The moderator, or <see langword="null"/> when the registry holds no evaluator under
    /// <see cref="ModerationEvaluatorName"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">The registry is <see langword="null"/>.</exception>
    /// <remarks>
    /// A host that registers no moderation evaluator runs every turn and refuses none, exactly as a
    /// host that binds no audit sink still answers a call. Moderation needs a vendor account, and a
    /// library that refused to run without one would be unusable in a test.
    /// </remarks>
    public static PromptModerator? FromRegistry(EvaluatorRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        return registry.TryGetEvaluator(ModerationEvaluatorName, out IEvaluator? evaluator) && evaluator is not null
            ? new PromptModerator(evaluator)
            : null;
    }

    /// <summary>Reads which categories flagged what the caller said.</summary>
    /// <param name="callerText">The words the caller spoke this turn.</param>
    /// <param name="cancellationToken">Cancels the endpoint call.</param>
    /// <returns>
    /// The categories, in the order the endpoint returned them, or an empty list when the endpoint
    /// flagged nothing.
    /// </returns>
    /// <exception cref="ArgumentNullException">The text is <see langword="null"/>.</exception>
    /// <remarks>
    /// <b>An endpoint that did not answer reads as "nothing flagged", and the turn goes on.</b> That
    /// is the fail-open rule, and it is deliberate: a vendor outage must not refuse every caller on
    /// a support line. <see cref="ModerationVerdict.TryRead"/> answers <see langword="false"/> for a
    /// failed evaluation, so the two cases meet here and the caller of this method cannot tell them
    /// apart. <see cref="Runtime.CallSession"/> counts them apart on a metric instead.
    /// </remarks>
    public async ValueTask<IReadOnlyList<string>> FlaggedCategoriesAsync(
        string callerText,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callerText);

        // The IEvaluator signature carries the text under test on the response, so the caller's words
        // ride there. The evaluator moderates whatever text it is given and never asks who said it.
        ChatResponse spoken = new(new ChatMessage(ChatRole.User, callerText));

        EvaluationResult result = await _evaluator
            .EvaluateAsync([], spoken, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return ModerationVerdict.TryRead(result, out ModerationVerdict? verdict) && verdict is { Flagged: true }
            ? verdict.Categories
            : NothingFlagged;
    }
}
