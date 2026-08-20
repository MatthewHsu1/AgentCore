using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace AgentCore.Application.Evaluation;

/// <summary>
/// Measures whether a reply states the fault codes access path 1 resolved, and no others.
/// </summary>
/// <remarks>
/// <para>
/// Decision D13 asks for custom evaluators, and names this one. It is written once and used twice:
/// the offline golden set in <c>tests/AgentCore.Evals</c> runs it over recorded rows, and the online
/// path runs it over a sampled turn when triage row T18 opens.
/// </para>
/// <para>
/// The evaluator measures two failures, and both come from section 9. A reply that omits the code the
/// lookup resolved sends the caller to the wrong page. A reply that states a code the lookup did not
/// resolve invented it, and D10 cannot catch that, because the lookup already succeeded.
/// </para>
/// <para>
/// It calls no model. The measurement is a set comparison over the reply text, so it costs nothing,
/// it never blocks, and it gives the same answer every time. That is why this evaluator, and not a
/// judge, guards path 1.
/// </para>
/// <para>
/// A code is compared with <see cref="StringComparison.Ordinal"/>, the same as every other name in
/// the library. The reply has to write the code the way the knowledge base writes it, which is what
/// the caller reads off the console.
/// </para>
/// </remarks>
public sealed class FaultCodeEvaluator : IEvaluator
{
    /// <summary>The name of the one metric this evaluator produces.</summary>
    public const string FaultCodeAccuracyMetricName = "Fault Code Accuracy";

    /// <summary>The shape a fault code takes when no host names another.</summary>
    /// <remarks>
    /// It matches one to three capital letters, an optional hyphen, and one to three digits, so
    /// <c>E7</c> and <c>LS-12</c> both count. The shape belongs to the knowledge base, not to this
    /// library, so a host with another convention passes its own pattern. A host whose model names
    /// take this shape too, such as <c>F85</c>, has to pass a narrower pattern, or every reply that
    /// names the machine reads as an invented code.
    /// </remarks>
    public const string DefaultCodePattern = @"\b[A-Z]{1,3}-?[0-9]{1,3}\b";

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    private readonly Regex _codeShape;

    /// <summary>Builds an evaluator that reads codes with <see cref="DefaultCodePattern"/>.</summary>
    public FaultCodeEvaluator()
        : this(DefaultCodePattern)
    {
    }

    /// <summary>Builds an evaluator that reads codes with the supplied pattern.</summary>
    /// <param name="codePattern">The shape a fault code takes in the knowledge base.</param>
    /// <exception cref="ArgumentException">The pattern is empty or does not compile.</exception>
    /// <remarks>
    /// The pattern is a C# value, not a configuration key. Decision D15 closes the configuration
    /// document, and no key describes a fault code today, so the host passes the shape in.
    /// </remarks>
    public FaultCodeEvaluator(string codePattern)
    {
        ArgumentException.ThrowIfNullOrEmpty(codePattern);
        _codeShape = new Regex(codePattern, RegexOptions.CultureInvariant, MatchTimeout);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> EvaluationMetricNames => [FaultCodeAccuracyMetricName];

    /// <inheritdoc />
    /// <remarks>
    /// The evaluation needs one <see cref="FaultCodeContext"/> in <paramref name="additionalContext"/>.
    /// Without it the evaluator cannot know what the lookup resolved, so it returns a metric with no
    /// value and an error diagnostic rather than a passing score.
    /// </remarks>
    public ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse modelResponse,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelResponse);
        cancellationToken.ThrowIfCancellationRequested();

        FaultCodeContext? context = additionalContext?.OfType<FaultCodeContext>().FirstOrDefault();
        if (context is null)
        {
            BooleanMetric unknown = new(FaultCodeAccuracyMetricName, value: null);
            unknown.AddDiagnostics(
                EvaluationDiagnostic.Error($"the evaluation needs one {nameof(FaultCodeContext)}."));
            unknown.Interpretation = new EvaluationMetricInterpretation(
                EvaluationRating.Inconclusive,
                failed: false,
                reason: "nothing named the codes access path 1 resolved.");

            return ValueTask.FromResult(new EvaluationResult(unknown));
        }

        string reply = modelResponse.Text;

        List<string> missing = [.. context.Codes.Where(code => !reply.Contains(code, StringComparison.Ordinal))];
        HashSet<string> resolved = new(context.Codes, StringComparer.Ordinal);
        List<string> invented =
        [
            .. _codeShape.Matches(reply)
                .Select(match => match.Value)
                .Where(cited => !resolved.Contains(cited))
                .Distinct(StringComparer.Ordinal),
        ];

        bool accurate = missing.Count == 0 && invented.Count == 0;
        BooleanMetric metric = new(FaultCodeAccuracyMetricName, accurate, Explain(missing, invented));
        metric.Interpretation = new EvaluationMetricInterpretation(
            accurate ? EvaluationRating.Exceptional : EvaluationRating.Unacceptable,
            failed: !accurate,
            reason: metric.Reason);

        return ValueTask.FromResult(new EvaluationResult(metric));
    }

    private static string Explain(List<string> missing, List<string> invented)
    {
        if (missing.Count == 0 && invented.Count == 0)
        {
            return "the reply states every code the lookup resolved, and no other.";
        }

        List<string> parts = [];
        if (missing.Count > 0)
        {
            parts.Add($"the reply omits {string.Join(", ", missing)}.");
        }

        if (invented.Count > 0)
        {
            parts.Add($"the reply states {string.Join(", ", invented)}, which the lookup did not resolve.");
        }

        return string.Join(" ", parts);
    }
}
