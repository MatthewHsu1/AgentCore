using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace AgentCore.Application.Tests.Evaluation.Fakes;

/// <summary>
/// An evaluator that answers with one fixed metric and counts its calls.
/// </summary>
/// <remarks>
/// The registry and the publisher hold evaluators and scores. Neither reads a real metric, so the
/// tests for them need an evaluator that calls no model.
/// </remarks>
public sealed class RecordingEvaluator : IEvaluator
{
    /// <summary>The name of the metric this evaluator produces.</summary>
    public const string MetricName = "Recorded";

    /// <summary>Gets the number of times a caller evaluated something.</summary>
    public int Calls { get; private set; }

    /// <inheritdoc />
    public IReadOnlyCollection<string> EvaluationMetricNames => [MetricName];

    /// <inheritdoc />
    public ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse modelResponse,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        return ValueTask.FromResult(new EvaluationResult(new NumericMetric(MetricName, Calls)));
    }
}
