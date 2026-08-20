using Microsoft.Extensions.AI.Evaluation;

namespace AgentCore.Application.Evaluation;

/// <summary>
/// What the moderation endpoint answered about one piece of text.
/// </summary>
/// <remarks>
/// <para>
/// Decision D13 makes <see cref="IEvaluator"/> the moderation port and refuses a second one, so the
/// verdict has to travel on the result the evaluator already returns. This type is that carrier. The
/// moderation adapter attaches it to its metric, and the caller reads it back through
/// <see cref="TryRead"/>. Nothing between the two parses a metric name, a reason, or a metadata
/// string.
/// </para>
/// <para>
/// It lives in <c>Application/Evaluation/</c> and not beside the adapter, for two reasons. D3 forbids
/// <c>AgentCore.Application</c> from referencing <c>AgentCore.Infrastructure</c>, and the code that
/// records a flagged turn sits in <see cref="Runtime.CallSession"/>, which is in Application. And
/// D13 promises that replacing OpenAI with a self-hosted classifier behind the same
/// <see cref="IEvaluator"/> is a one-file change, which holds only while this type names no vendor.
/// </para>
/// <para>
/// The categories keep the order the endpoint returned them in, because
/// <see cref="Domain.Audit.AuditPayloadKeys.ModerationCategories"/> promises that order to a reader
/// of the audit chain. Nothing here sorts them, renames them, or checks them against a list: the
/// taxonomy belongs to the endpoint and it is open. A category this library never named still
/// reaches the chain.
/// </para>
/// <para>
/// <see cref="FaultCodeContext"/> is the shape this follows: an <see cref="EvaluationContext"/> that
/// carries a typed payload beside a metric.
/// </para>
/// </remarks>
public sealed class ModerationVerdict : EvaluationContext
{
    /// <summary>The name this verdict carries on the metric.</summary>
    public const string ModerationVerdictName = "Moderation Verdict";

    /// <summary>The text a verdict with no flagged category carries as its content.</summary>
    public const string NothingFlagged = "the endpoint flagged no category.";

    /// <summary>Builds a verdict from what the endpoint answered.</summary>
    /// <param name="flagged">Whether the endpoint flagged the text.</param>
    /// <param name="categories">
    /// The categories the endpoint flagged, in the order it returned them. It is empty when the
    /// endpoint flagged nothing.
    /// </param>
    /// <exception cref="ArgumentNullException">The categories are <see langword="null"/>.</exception>
    /// <remarks>
    /// The endpoint owns <paramref name="flagged"/>, and this type never recomputes it from
    /// <paramref name="categories"/>. A model that flags text without naming a category still reads
    /// as flagged, and a threshold the vendor moves does not drift the answer.
    /// </remarks>
    public ModerationVerdict(bool flagged, IEnumerable<string> categories)
        : this(flagged, Materialize(categories))
    {
    }

    private ModerationVerdict(bool flagged, string[] categories)
        : base(ModerationVerdictName, categories.Length == 0 ? NothingFlagged : string.Join(", ", categories))
    {
        Flagged = flagged;
        Categories = categories;
    }

    /// <summary>Gets whether the endpoint flagged the text.</summary>
    public bool Flagged { get; }

    /// <summary>Gets the categories the endpoint flagged, in the order it returned them.</summary>
    public IReadOnlyList<string> Categories { get; }

    /// <summary>Reads the verdict one evaluation carries.</summary>
    /// <param name="result">What the moderation evaluator returned.</param>
    /// <param name="verdict">The verdict, when one metric carries it.</param>
    /// <returns><see langword="true"/> when the endpoint answered.</returns>
    /// <exception cref="ArgumentNullException">The result is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// <b>A verdict is present only when the endpoint answered.</b> An evaluation that failed, timed
    /// out, or read a body it could not parse carries an inconclusive metric and no verdict, so this
    /// answers <see langword="false"/>. The caller then knows the text is unchecked, which is a
    /// different fact from "the endpoint checked it and flagged nothing".
    /// </para>
    /// <para>
    /// It searches every metric and names none, so the caller compiles against this assembly alone
    /// and never against the adapter that produced the result.
    /// </para>
    /// </remarks>
    public static bool TryRead(EvaluationResult result, out ModerationVerdict? verdict)
    {
        ArgumentNullException.ThrowIfNull(result);

        foreach (EvaluationMetric metric in result.Metrics.Values)
        {
            if (metric.Context?.TryGetValue(ModerationVerdictName, out EvaluationContext? context) == true
                && context is ModerationVerdict found)
            {
                verdict = found;
                return true;
            }
        }

        verdict = null;
        return false;
    }

    private static string[] Materialize(IEnumerable<string> categories)
    {
        ArgumentNullException.ThrowIfNull(categories);
        return [.. categories];
    }
}
