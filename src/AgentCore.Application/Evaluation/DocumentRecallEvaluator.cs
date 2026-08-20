using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace AgentCore.Application.Evaluation;

/// <summary>
/// Measures the share of the expected documents one search returned.
/// </summary>
/// <remarks>
/// <para>
/// This is the retrieval half of the offline set of D13. It answers one question: did the search
/// return the file that holds the answer. It does not read the passage, and it does not read the
/// reply. <see cref="FaultCodeEvaluator"/> reads the reply.
/// </para>
/// <para>
/// It calls no model, so it runs where no key is set. That is what makes it the metric a pull request
/// can gate on, and it is the same reason <see cref="FaultCodeEvaluator"/> guards path 1 rather than a
/// judge.
/// </para>
/// <para>
/// The score is recall and never precision. A store that returns the answer plus four other files
/// still answered the question, because the model reads what comes back and the reply is what section
/// 9 measures. A store that returns the answer nowhere in the first <c>limit</c> hits has failed, and
/// no later metric can recover from it.
/// </para>
/// <para>
/// A document id is compared with <see cref="StringComparison.Ordinal"/>, the same as every other name
/// in the library.
/// </para>
/// </remarks>
public sealed class DocumentRecallEvaluator : IEvaluator
{
    /// <summary>The name of the one metric this evaluator produces.</summary>
    public const string DocumentRecallMetricName = "Document Recall";

    /// <inheritdoc />
    public IReadOnlyCollection<string> EvaluationMetricNames => [DocumentRecallMetricName];

    /// <inheritdoc />
    /// <remarks>
    /// The evaluation needs one <see cref="RetrievedDocumentsContext"/> in
    /// <paramref name="additionalContext"/>, and that context has to name at least one expected
    /// document. Without it the evaluator returns a metric with no value and an error diagnostic,
    /// rather than a passing score: a row that expects nothing measures nothing.
    /// </remarks>
    public ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse modelResponse,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        RetrievedDocumentsContext? context =
            additionalContext?.OfType<RetrievedDocumentsContext>().FirstOrDefault();

        if (context is null || context.Expected.Count == 0)
        {
            NumericMetric unknown = new(DocumentRecallMetricName, value: null);
            unknown.AddDiagnostics(EvaluationDiagnostic.Error(
                $"the evaluation needs one {nameof(RetrievedDocumentsContext)} that names an expected document."));
            unknown.Interpretation = new EvaluationMetricInterpretation(
                EvaluationRating.Inconclusive,
                failed: false,
                reason: "nothing named the documents the row expects.");

            return ValueTask.FromResult(new EvaluationResult(unknown));
        }

        HashSet<string> returned = new(context.Retrieved, StringComparer.Ordinal);
        List<string> missed = [.. context.Expected.Where(id => !returned.Contains(id))];

        double recall = (context.Expected.Count - missed.Count) / (double)context.Expected.Count;

        NumericMetric metric = new(DocumentRecallMetricName, recall, Explain(missed));
        metric.Interpretation = new EvaluationMetricInterpretation(
            Rate(recall),
            failed: missed.Count > 0,
            reason: metric.Reason);

        return ValueTask.FromResult(new EvaluationResult(metric));
    }

    private static EvaluationRating Rate(double recall) => recall switch
    {
        >= 1.0 => EvaluationRating.Exceptional,
        >= 0.5 => EvaluationRating.Poor,
        > 0.0 => EvaluationRating.Unacceptable,
        _ => EvaluationRating.Unacceptable,
    };

    private static string Explain(List<string> missed)
        => missed.Count == 0
            ? "the search returned every document the row expects."
            : $"the search did not return {string.Join(", ", missed)}.";
}
