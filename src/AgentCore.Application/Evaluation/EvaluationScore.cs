using Microsoft.Extensions.AI.Evaluation;

namespace AgentCore.Application.Evaluation;

/// <summary>
/// One finished evaluation, ready to leave the library.
/// </summary>
/// <remarks>
/// The score names the evaluator that produced it, because one turn may pass through several and a
/// reader has to tell the metrics apart. It carries nothing else. The library holds no domain type
/// under D15, so a turn identifier and a timestamp belong to whatever the host publishes into.
/// </remarks>
/// <param name="Evaluator">The name the evaluator carries in <see cref="EvaluatorRegistry"/>.</param>
/// <param name="Result">The metrics the evaluator produced.</param>
public sealed record EvaluationScore(string Evaluator, EvaluationResult Result);
